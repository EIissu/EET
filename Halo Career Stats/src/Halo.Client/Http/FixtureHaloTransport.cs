using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eet.Halo.Client.Endpoints;
using Eet.Trackers.Core;

namespace Eet.Halo.Client.Http;

/// <summary>
/// Serves the recorded fixtures, so the whole tracker works with zero credentials.
///
/// Two design points are load-bearing:
///
///   * The fixtures are raw API-shaped JSON, so this class hands back bytes and nothing
///     else. Every line of deserialisation, enum handling and mapping downstream of here
///     is the same code the live path runs. A fixture that returned a pre-baked
///     CareerSnapshot would make the no-credentials path a demo; this makes it a test.
///
///   * Pagination is honoured rather than faked. The match-history fixture holds one large
///     recorded page and this slices it by the <c>start</c> and <c>count</c> the caller
///     asked for, so the client's paging loop is genuinely exercised offline. A fixture
///     that ignored paging would let an off-by-one in that loop reach production.
///
/// Multi-response endpoints (match stats, per-match skill, UGC assets) are stored as a
/// recorded bundle: a <c>Recorded</c> object keyed by the id in the request path, where
/// each value is exactly one raw response. One file per match would be 120 files.
/// </summary>
public sealed class FixtureHaloTransport : IHaloTransport
{
    /// <summary>The bundle envelope's payload property. Each value is a verbatim raw response.</summary>
    private const string RecordedProperty = "Recorded";

    private readonly string _directory;
    private readonly Dictionary<string, JsonNode?> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public FixtureHaloTransport(string directory)
    {
        _directory = directory;
        if (!Directory.Exists(directory))
        {
            throw new TrackerException(
                $"Fixture directory '{directory}' does not exist.",
                "Fixtures live in Career Stats Shared/fixtures. Set Halo:FixtureDirectory if the app is running from somewhere unusual.");
        }
    }

    public bool IsFixture => true;

    public string Description => "synthetic fixtures (no credentials configured)";

    public string FixtureDirectory => _directory;

    /// <summary>
    /// Find the fixture directory from wherever the process happens to be running.
    /// Probes the configured path against the content root, then walks up towards the
    /// repository root, because `dotnet run` and `dotnet test` have very different ideas
    /// about the current directory.
    /// </summary>
    public static string? Locate(string configuredPath, string contentRoot)
    {
        if (Path.IsPathRooted(configuredPath))
        {
            return Directory.Exists(configuredPath) ? configuredPath : null;
        }

        var start = new DirectoryInfo(contentRoot);
        for (var dir = start; dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, configuredPath);
            if (Directory.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    public async Task<string> GetJsonAsync(HaloCall call, CancellationToken ct = default)
    {
        var json = await TryGetJsonAsync(call, ct).ConfigureAwait(false);
        return json ?? throw new TrackerException(
            $"No fixture recorded for {call!.Endpoint.Id} ({call.PathAndQuery}).",
            $"Add the recorded response to {_directory}. Fixtures must be raw API-shaped JSON, the same bytes the service would have returned.");
    }

    public Task<string?> TryGetJsonAsync(HaloCall call, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        ct.ThrowIfCancellationRequested();

        var json = call.Endpoint.Id switch
        {
            HaloEndpointIds.MatchHistory => Page(call),
            HaloEndpointIds.MatchCount => Whole("halo-match-count.json"),
            HaloEndpointIds.MatchStats => Recorded("halo-match-stats.json", call, "matchId"),
            HaloEndpointIds.MatchSkill => Recorded("halo-match-skill.json", call, "matchId"),
            HaloEndpointIds.PlaylistCsr => Whole("halo-playlist-csr.json"),
            HaloEndpointIds.UgcMap => Recorded("halo-ugc-assets.json", call, "assetId"),
            HaloEndpointIds.UgcGameVariant => Recorded("halo-ugc-assets.json", call, "assetId"),
            HaloEndpointIds.UgcPlaylist => Recorded("halo-ugc-assets.json", call, "assetId"),
            HaloEndpointIds.ServiceRecord => Whole("halo-service-record.json"),
            HaloEndpointIds.Clearance => Whole("halo-clearance.json"),
            _ => null,
        };

        return Task.FromResult(json);
    }

    /// <summary>The Xbox profile lookup, which is not a Halo endpoint and so is not a HaloCall.</summary>
    public string? TryGetProfileJson() => Whole("halo-xbox-profile.json");

    private string? Whole(string fileName) => Load(fileName)?.ToJsonString(WriteOptions);

    /// <summary>
    /// Slice the recorded match-history page the way the service would.
    ///
    /// Results are newest-first on the wire, which is what makes <c>start</c> a stable
    /// cursor for a career that is still being played: page 0 is always the most recent
    /// matches.
    /// </summary>
    private string? Page(HaloCall call)
    {
        if (Load("halo-match-history.json") is not JsonObject root)
        {
            return null;
        }

        var all = root["Results"] as JsonArray ?? [];
        var start = QueryInt(call, "start", 0);
        var count = QueryInt(call, "count", 25);

        var slice = new JsonArray();
        for (var i = start; i < Math.Min(all.Count, start + count); i++)
        {
            slice.Add(all[i]?.DeepClone());
        }

        var page = new JsonObject
        {
            ["Start"] = start,
            ["Count"] = count,
            ["ResultCount"] = slice.Count,
            ["Results"] = slice,
            ["Links"] = new JsonObject(),
        };

        if (root["_note"] is { } note)
        {
            page["_note"] = note.DeepClone();
        }

        return page.ToJsonString(WriteOptions);
    }

    private string? Recorded(string fileName, HaloCall call, string pathArgName)
    {
        if (!call.PathArgs.TryGetValue(pathArgName, out var key) || Load(fileName) is not JsonObject root)
        {
            return null;
        }

        if (root[RecordedProperty] is not JsonObject recorded)
        {
            throw new TrackerException(
                $"Fixture '{fileName}' has no '{RecordedProperty}' object.",
                $"A multi-response fixture is an envelope: {{ \"_note\": ..., \"{RecordedProperty}\": {{ \"<id>\": <raw response> }} }}.");
        }

        // Asset ids and match ids are GUIDs, and GUID casing is not something either side
        // is careful about.
        foreach (var (id, value) in recorded)
        {
            if (string.Equals(id, key, StringComparison.OrdinalIgnoreCase))
            {
                return value?.ToJsonString(WriteOptions);
            }
        }

        return null;
    }

    private static int QueryInt(HaloCall call, string name, int fallback)
    {
        foreach (var (key, value) in call.Query)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return Math.Max(0, parsed);
            }
        }

        return fallback;
    }

    private JsonNode? Load(string fileName)
    {
        lock (_gate)
        {
            if (_files.TryGetValue(fileName, out var cached))
            {
                return cached;
            }

            var path = Path.Combine(_directory, fileName);
            JsonNode? node = null;
            if (File.Exists(path))
            {
                try
                {
                    node = JsonNode.Parse(
                        File.ReadAllText(path),
                        documentOptions: new JsonDocumentOptions
                        {
                            CommentHandling = JsonCommentHandling.Skip,
                            AllowTrailingCommas = true,
                        });
                }
                catch (JsonException ex)
                {
                    throw new TrackerException(
                        $"Fixture '{path}' is not valid JSON: {ex.Message}",
                        "Fix the fixture. It must be raw API-shaped JSON, optionally with a leading \"_note\" marking it synthetic.",
                        ex);
                }
            }

            _files[fileName] = node;
            return node;
        }
    }

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = false };
}
