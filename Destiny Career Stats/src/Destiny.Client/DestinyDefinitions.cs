using System.Globalization;
using System.Text.Json;
using Eet.Trackers.Core;

namespace Eet.Destiny.Client;

/// <summary>
/// Turning the hashes in a match record into words. A match that says
/// <c>referenceId 2570313480, mode 43</c> is useless on a dashboard; "Bannerfall, Iron
/// Banner Control" is the whole point.
/// </summary>
public interface IDestinyDefinitions
{
    /// <summary>Manifest version these definitions came from, or null when none loaded.</summary>
    string? Version { get; }

    bool IsLoaded { get; }

    /// <summary>Activity name, which for Crucible is the map. Null when unknown.</summary>
    string? ActivityName(uint hash);

    string? ActivityIconUrl(uint hash);

    /// <summary>
    /// Whether the activity is Crucible. Null when unknown, which matters: an unknown
    /// activity must not be recorded as a PvE match with no win or loss, nor as a PvP match
    /// with a fabricated one.
    /// </summary>
    bool? ActivityIsPvp(uint hash);

    /// <summary>Human label for a DestinyActivityModeType value.</summary>
    string ModeName(int modeType);

    /// <summary>0 None, 1 PvE, 2 PvP, 3 PvE competitive. Null when unknown.</summary>
    int? ModeCategory(int modeType);

    /// <summary>True for umbrella modes such as AllPvP, which never label a single match well.</summary>
    bool IsAggregateMode(int modeType);
}

/// <summary>
/// The definition tables this tracker needs, cached on disk and keyed by manifest version.
///
/// Deliberately not the world SQLite file. That download is enormous, and this tracker
/// needs exactly two tables out of it: activity names and mode names. Bungie publishes each
/// table separately under <c>jsonWorldComponentContentPaths</c>, so the cost is a few
/// hundred kilobytes instead of a hundred megabytes.
///
/// What lands on disk is a projection, not the raw table -- name, icon, PvP flag -- because
/// DestinyActivityDefinition is tens of megabytes of matchmaking rules and modifier
/// references that no career page will ever read. The version string from the manifest is
/// the cache key, and it is the only thing that can invalidate the cache: definition
/// content is immutable for a given version.
/// </summary>
public sealed class DestinyManifestCache : IDestinyDefinitions
{
    private const string ActivityTable = "DestinyActivityDefinition";
    private const string ModeTable = "DestinyActivityModeDefinition";

    private readonly IBungieApi _api;
    private readonly BungieOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Dictionary<uint, ActivityRow> _activities = [];
    private Dictionary<int, ModeRow> _modes = [];

    public DestinyManifestCache(IBungieApi api, BungieOptions options)
    {
        _api = api;
        _options = options;
    }

    public string? Version { get; private set; }

    public bool IsLoaded => _activities.Count > 0 || _modes.Count > 0;

    /// <summary>Where the projection for the loaded version lives. Null before a load.</summary>
    public string? CachePath { get; private set; }

    /// <summary>
    /// True when the last load was served entirely from disk. Worth knowing: it is the
    /// difference between one small request and a multi-megabyte download.
    /// </summary>
    public bool LoadedFromCache { get; private set; }

    /// <summary>
    /// Load the definitions, from disk when the manifest version has not moved.
    ///
    /// Best effort by design. A tracker that cannot name a map is mildly worse; a tracker
    /// that refuses to show a career because a CDN file 404'd is useless. Failures come back
    /// as a warning string rather than an exception.
    /// </summary>
    /// <returns>A warning to surface on the snapshot, or null when everything loaded.</returns>
    public async Task<string?> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var manifest = await _api.GetManifestAsync(ct).ConfigureAwait(false);
            var version = manifest.Version;
            if (string.IsNullOrWhiteSpace(version))
            {
                return "The Destiny manifest came back without a version, so definitions were not "
                    + "cached. Map and mode names fall back to their built-in labels.";
            }

            if (string.Equals(version, Version, StringComparison.Ordinal) && IsLoaded)
            {
                LoadedFromCache = true;
                return null;
            }

            var directory = Path.Combine(_options.CacheDirectory, "manifest", Sanitise(version));
            var activityFile = Path.Combine(directory, "activities.json");
            var modeFile = Path.Combine(directory, "modes.json");

            if (File.Exists(activityFile) && File.Exists(modeFile))
            {
                _activities = ReadProjection<ActivityRow>(activityFile).ToDictionary(
                    kv => uint.Parse(kv.Key, CultureInfo.InvariantCulture), kv => kv.Value);
                _modes = ReadProjection<ModeRow>(modeFile).ToDictionary(
                    kv => int.Parse(kv.Key, CultureInfo.InvariantCulture), kv => kv.Value);

                Version = version;
                CachePath = directory;
                LoadedFromCache = true;
                return null;
            }

            if (manifest.JsonWorldComponentContentPaths is null
                || !TryGetTablePaths(manifest, _options.Locale, out var activityPath, out var modePath))
            {
                return $"The manifest has no {ActivityTable} or {ModeTable} entry for locale "
                    + $"'{_options.Locale}'. Map names will be unavailable; check the locale, since "
                    + "jsonWorldComponentContentPaths is keyed by it.";
            }

            _activities = await ProjectActivitiesAsync(activityPath, ct).ConfigureAwait(false);
            _modes = await ProjectModesAsync(modePath, ct).ConfigureAwait(false);

            Version = version;
            CachePath = directory;
            LoadedFromCache = false;

            WriteProjection(directory, activityFile, _activities.ToDictionary(
                kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => kv.Value));
            WriteProjection(directory, modeFile, _modes.ToDictionary(
                kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => kv.Value));

            PruneOtherVersions(Path.Combine(_options.CacheDirectory, "manifest"), directory);
            return null;
        }
        catch (TrackerException ex)
        {
            return $"Destiny definitions are unavailable, so map names fall back to hashes. {ex.Message}";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException
                                       or UnauthorizedAccessException or FormatException)
        {
            return $"Destiny definitions could not be loaded ({ex.GetType().Name}: {ex.Message}). "
                + "Map names fall back to their hashes; everything else is unaffected.";
        }
        finally
        {
            _gate.Release();
        }
    }

    public string? ActivityName(uint hash) =>
        _activities.TryGetValue(hash, out var row) && !string.IsNullOrWhiteSpace(row.Name)
            ? row.Name
            : null;

    public string? ActivityIconUrl(uint hash) =>
        _activities.TryGetValue(hash, out var row) && !string.IsNullOrWhiteSpace(row.Icon)
            ? Absolute(row.Icon)
            : null;

    public bool? ActivityIsPvp(uint hash) =>
        _activities.TryGetValue(hash, out var row) ? row.IsPvp : null;

    public string ModeName(int modeType) =>
        _modes.TryGetValue(modeType, out var row) && !string.IsNullOrWhiteSpace(row.Name)
            ? row.Name
            : DestinyActivityMode.Label(modeType);

    public int? ModeCategory(int modeType) =>
        _modes.TryGetValue(modeType, out var row) ? row.Category : DestinyActivityMode.Category(modeType);

    public bool IsAggregateMode(int modeType) =>
        _modes.TryGetValue(modeType, out var row) ? row.IsAggregate : DestinyActivityMode.IsAggregate(modeType);

    private string Absolute(string path) =>
        path.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? path
            : new Uri(new Uri(_options.ContentBaseUrl, UriKind.Absolute), path).ToString();

    private static bool TryGetTablePaths(
        DestinyManifest manifest, string locale, out string activityPath, out string modePath)
    {
        activityPath = string.Empty;
        modePath = string.Empty;

        var paths = manifest.JsonWorldComponentContentPaths;
        if (paths is null)
        {
            return false;
        }

        // Locale keys are exact in the manifest, but a caller writing "EN" should still work.
        var table = paths.FirstOrDefault(p => string.Equals(p.Key, locale, StringComparison.OrdinalIgnoreCase)).Value
            ?? (paths.TryGetValue("en", out var fallback) ? fallback : null);

        return table is not null
            && table.TryGetValue(ActivityTable, out activityPath!)
            && table.TryGetValue(ModeTable, out modePath!)
            && !string.IsNullOrWhiteSpace(activityPath)
            && !string.IsNullOrWhiteSpace(modePath);
    }

    private async Task<Dictionary<uint, ActivityRow>> ProjectActivitiesAsync(string path, CancellationToken ct)
    {
        var rows = new Dictionary<uint, ActivityRow>();
        await using var stream = await _api.GetDefinitionTableAsync(path, ct).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        foreach (var property in document.RootElement.EnumerateObject())
        {
            // Fixture tables carry a _note key marking them synthetic; real tables are keyed
            // entirely by hash. Either way, a key that is not a hash is not a definition.
            if (!uint.TryParse(property.Name, CultureInfo.InvariantCulture, out var hash))
            {
                continue;
            }

            var definition = property.Value.Deserialize<ActivityDefinition>(BungieResponse.Json);
            if (definition is null)
            {
                continue;
            }

            var name = definition.DisplayProperties?.Name
                ?? definition.OriginalDisplayProperties?.Name
                ?? definition.SelectionScreenDisplayProperties?.Name;

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            rows[hash] = new ActivityRow(
                name,
                definition.DisplayProperties?.Icon,
                definition.IsPvP,
                definition.DirectActivityModeType);
        }

        return rows;
    }

    private async Task<Dictionary<int, ModeRow>> ProjectModesAsync(string path, CancellationToken ct)
    {
        var rows = new Dictionary<int, ModeRow>();
        await using var stream = await _api.GetDefinitionTableAsync(path, ct).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!uint.TryParse(property.Name, CultureInfo.InvariantCulture, out _))
            {
                continue;
            }

            var definition = property.Value.Deserialize<ActivityModeDefinition>(BungieResponse.Json);
            var name = definition?.DisplayProperties?.Name;
            if (definition is null || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            // Keyed by modeType, not by hash: match records carry the enum value, never the
            // definition hash.
            rows[definition.ModeType] = new ModeRow(
                name, definition.ActivityModeCategory, definition.IsAggregateMode);
        }

        return rows;
    }

    private static Dictionary<string, T> ReadProjection<T>(string file)
    {
        using var stream = File.OpenRead(file);
        return JsonSerializer.Deserialize<Dictionary<string, T>>(stream, BungieResponse.Json) ?? [];
    }

    private static void WriteProjection<T>(string directory, string file, Dictionary<string, T> rows)
    {
        try
        {
            Directory.CreateDirectory(directory);
            // Written to a temporary file first so a killed process cannot leave a truncated
            // cache that then looks valid on the next run.
            var temporary = file + ".tmp";
            using (var stream = File.Create(temporary))
            {
                JsonSerializer.Serialize(stream, rows, BungieResponse.Json);
            }

            File.Move(temporary, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read-only or full cache directory is not a reason to fail a career lookup.
            // Everything needed is already in memory.
        }
    }

    private static void PruneOtherVersions(string root, string keep)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                if (!string.Equals(
                        Path.GetFullPath(directory), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Stale cache directories waste disk, nothing more.
        }
    }

    private static string Sanitise(string version)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(version.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return cleaned.Length > 96 ? cleaned[..96] : cleaned;
    }

    private sealed record ActivityRow(string Name, string? Icon, bool IsPvp, int ModeType);

    private sealed record ModeRow(string Name, int Category, bool IsAggregate);
}

/// <summary>
/// Built-in labels for DestinyActivityModeType, used when the manifest is unavailable and
/// as the source of truth for which modes are Crucible.
///
/// Being able to answer "is this PvP" without the manifest matters more than it looks: it
/// decides whether a match has a win or a loss at all, and getting it wrong turns a strike
/// into a defeat.
/// </summary>
public static class DestinyActivityMode
{
    public const int None = 0;
    public const int Story = 2;
    public const int Strike = 3;
    public const int Raid = 4;
    public const int AllPvP = 5;
    public const int Patrol = 6;
    public const int AllPvE = 7;
    public const int Control = 10;
    public const int Clash = 12;
    public const int Nightfall = 16;
    public const int AllStrikes = 18;
    public const int IronBanner = 19;
    public const int Supremacy = 31;
    public const int Survival = 37;
    public const int Countdown = 38;
    public const int Social = 40;
    public const int IronBannerControl = 43;
    public const int IronBannerClash = 44;
    public const int ScoredNightfall = 46;
    public const int Rumble = 48;
    public const int Gambit = 63;
    public const int AllPvECompetitive = 64;
    public const int Breakthrough = 65;
    public const int PvPCompetitive = 69;
    public const int PvPQuickplay = 70;
    public const int Elimination = 80;
    public const int Dungeon = 82;
    public const int TrialsOfOsiris = 84;
    public const int Dares = 85;
    public const int LostSector = 87;

    private static readonly Dictionary<int, (string Label, int Category, bool Aggregate)> Table = new()
    {
        [None] = ("Unknown", 0, false),
        [Story] = ("Story", 1, false),
        [Strike] = ("Strike", 1, false),
        [Raid] = ("Raid", 1, false),
        [AllPvP] = ("All PvP", 2, true),
        [Patrol] = ("Patrol", 1, false),
        [AllPvE] = ("All PvE", 1, true),
        [Control] = ("Control", 2, false),
        [Clash] = ("Clash", 2, false),
        [Nightfall] = ("Nightfall", 1, false),
        [17] = ("Heroic Nightfall", 1, false),
        [AllStrikes] = ("All Strikes", 1, true),
        [IronBanner] = ("Iron Banner", 2, true),
        [25] = ("Mayhem", 2, true),
        [Supremacy] = ("Supremacy", 2, false),
        [32] = ("Private Matches", 2, true),
        [Survival] = ("Survival", 2, false),
        [Countdown] = ("Countdown", 2, false),
        [39] = ("Trials of the Nine", 2, true),
        [Social] = ("Social", 1, false),
        [IronBannerControl] = ("Iron Banner Control", 2, false),
        [IronBannerClash] = ("Iron Banner Clash", 2, false),
        [45] = ("Iron Banner Supremacy", 2, false),
        [ScoredNightfall] = ("Nightfall", 1, false),
        [47] = ("Heroic Nightfall", 1, false),
        [Rumble] = ("Rumble", 2, false),
        [49] = ("Doubles", 2, true),
        [50] = ("Doubles", 2, false),
        [58] = ("Heroic Adventure", 1, false),
        [59] = ("Showdown", 2, false),
        [60] = ("Lockdown", 2, false),
        [61] = ("Scorched", 2, false),
        [62] = ("Scorched Team", 2, false),
        [Gambit] = ("Gambit", 3, false),
        [AllPvECompetitive] = ("All PvE Competitive", 3, true),
        [Breakthrough] = ("Breakthrough", 2, false),
        [67] = ("Salvage", 2, false),
        [PvPCompetitive] = ("Competitive", 2, true),
        [PvPQuickplay] = ("Quickplay", 2, true),
        [75] = ("Gambit Prime", 3, false),
        [76] = ("Reckoning", 3, false),
        [77] = ("Menagerie", 1, false),
        [79] = ("Nightmare Hunt", 1, false),
        [Elimination] = ("Elimination", 2, false),
        [81] = ("Momentum Control", 2, false),
        [Dungeon] = ("Dungeon", 1, false),
        [83] = ("The Sundial", 1, false),
        [TrialsOfOsiris] = ("Trials of Osiris", 2, false),
        [Dares] = ("Dares of Eternity", 1, false),
        [86] = ("Offensive", 1, false),
        [LostSector] = ("Lost Sector", 1, false),
    };

    /// <summary>A label a player would recognise, or the numeric mode when there is none.</summary>
    public static string Label(int modeType) =>
        Table.TryGetValue(modeType, out var row)
            ? row.Label
            : string.Create(CultureInfo.InvariantCulture, $"Mode {modeType}");

    /// <summary>0 None, 1 PvE, 2 PvP, 3 PvE competitive. Null when the mode is unrecognised.</summary>
    public static int? Category(int modeType) =>
        Table.TryGetValue(modeType, out var row) ? row.Category : null;

    public static bool IsAggregate(int modeType) =>
        Table.TryGetValue(modeType, out var row) && row.Aggregate;

    /// <summary>
    /// Pick the label-worthy mode out of an activity's mode list. Bungie does not promise an
    /// order, so this takes the most specific non-aggregate entry: "Iron Banner Control"
    /// beats "Control", which beats "All PvP".
    /// </summary>
    public static int MostSpecific(int mode, IReadOnlyList<int>? modes, IDestinyDefinitions definitions)
    {
        if (mode != None && !definitions.IsAggregateMode(mode))
        {
            return mode;
        }

        if (modes is not null)
        {
            foreach (var candidate in modes)
            {
                if (candidate != None && !definitions.IsAggregateMode(candidate))
                {
                    return candidate;
                }
            }

            foreach (var candidate in modes)
            {
                if (candidate != None)
                {
                    return candidate;
                }
            }
        }

        return mode;
    }
}
