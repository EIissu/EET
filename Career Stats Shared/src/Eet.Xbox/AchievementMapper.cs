using System.Globalization;
using System.Text.Json;
using Eet.Trackers.Core;
using Eet.Xbox.Wire;

namespace Eet.Xbox;

/// <summary>
/// One title's standing, plus the metadata the title hub knows and the achievements
/// endpoint does not.
/// </summary>
/// <param name="LastPlayed">From the title hub. Null when the title hub was not consulted.</param>
public sealed record TitleMetadata(
    string TitleId,
    string Name,
    DateTimeOffset? LastPlayed,
    string? DisplayImage,
    int? TotalGamerscore,
    int? TotalAchievements);

/// <summary>A player as Xbox describes them, before the shared model gets hold of them.</summary>
public sealed record XboxProfile(string Xuid, string Gamertag, int Gamerscore, string? IconUrl)
{
    /// <summary>Into the shared <see cref="Player"/>, which keys on the XUID rather than the tag.</summary>
    public Player ToPlayer() => new(Gamertag, Xuid, "Xbox", IconUrl);
}

/// <summary>
/// Raw Xbox JSON into the records in <c>Contracts.cs</c>.
///
/// Deliberately pure and public: it takes JSON text and returns model objects, touching no
/// HTTP at all. That is what lets the fixture path exercise the same mapping the live path
/// uses -- a fixture that is a pre-baked <c>TitleAchievements</c> would prove nothing,
/// whereas a fixture that is raw API-shaped JSON runs through every line below.
///
/// The awkward parts of the Xbox shape, all of which are handled here rather than at the
/// call sites:
///
///   * Gamerscore is not a field. It is an entry in "rewards" whose "type" is "Gamerscore"
///     and whose "value" is a STRING, so it needs invariant parsing or a French machine
///     reads "10" differently from an American one.
///   * "progressState" is the string "Achieved", not a boolean.
///   * A locked achievement still carries a "timeUnlocked" -- of 0001-01-01, which is a
///     null wearing a costume and would render as "unlocked in the year 1" if trusted.
///   * "description" is what the achievement means; "lockedDescription" is how to get it.
///     A dashboard showing locked achievements wants the second one.
///   * "rarity" is absent on older titles, so rare-ness is a tri-state, not a boolean.
/// </summary>
public static class AchievementMapper
{
    /// <summary>Xbox's own word for a completed achievement.</summary>
    private const string AchievedState = "Achieved";

    /// <summary>Map one page of the achievements response.</summary>
    public static IReadOnlyList<Achievement> MapAchievements(string json, string? fallbackTitleId = null)
    {
        var parsed = Deserialize<AchievementsResponse>(json);
        return MapAchievements(parsed, fallbackTitleId);
    }

    internal static IReadOnlyList<Achievement> MapAchievements(
        AchievementsResponse? response,
        string? fallbackTitleId)
    {
        if (response?.Achievements is null)
        {
            return Array.Empty<Achievement>();
        }

        var mapped = new List<Achievement>(response.Achievements.Count);
        foreach (var wire in response.Achievements)
        {
            mapped.Add(MapAchievement(wire, fallbackTitleId));
        }

        return mapped;
    }

    internal static Achievement MapAchievement(AchievementWire wire, string? fallbackTitleId)
    {
        var association = wire.TitleAssociations?.FirstOrDefault();
        var unlocked = string.Equals(wire.ProgressState, AchievedState, StringComparison.OrdinalIgnoreCase);

        // Locked achievements carry the 0001-01-01 sentinel, which ParseTimestamp discards,
        // but belt and braces: a locked achievement has no unlock time by definition.
        var unlockedAt = unlocked ? XboxJson.ParseTimestamp(wire.Progression?.TimeUnlocked) : null;

        return new Achievement(
            Id: wire.Id ?? string.Empty,
            TitleId: association is not null
                ? association.Id.ToString(CultureInfo.InvariantCulture)
                : fallbackTitleId ?? string.Empty,
            TitleName: association?.Name ?? string.Empty,
            Name: wire.Name ?? string.Empty,
            Description: ChooseDescription(wire, unlocked),
            Gamerscore: Gamerscore(wire),
            Unlocked: unlocked,
            ProgressPercent: Progress(wire, unlocked),
            UnlockedAt: unlockedAt,
            IsRare: string.Equals(wire.Rarity?.CurrentCategory, "Rare", StringComparison.OrdinalIgnoreCase),
            RarityPercent: wire.Rarity?.CurrentPercentage,
            IconUrl: IconUrl(wire));
    }

    /// <summary>
    /// Roll a set of mapped achievements into the title-level record, folding in whatever
    /// the title hub knew. The title hub's totals win where present because they count
    /// achievements added after the page we fetched; ours are the fallback.
    /// </summary>
    public static TitleAchievements Summarise(
        string titleId,
        IReadOnlyList<Achievement> achievements,
        TitleMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(achievements);

        var earnedGamerscore = 0;
        var totalGamerscore = 0;
        var earnedCount = 0;
        DateTimeOffset? latestUnlock = null;

        foreach (var achievement in achievements)
        {
            totalGamerscore += achievement.Gamerscore;

            if (!achievement.Unlocked)
            {
                continue;
            }

            earnedGamerscore += achievement.Gamerscore;
            earnedCount++;

            if (achievement.UnlockedAt is { } at && (latestUnlock is null || at > latestUnlock))
            {
                latestUnlock = at;
            }
        }

        var name = metadata?.Name;
        if (string.IsNullOrEmpty(name))
        {
            name = achievements.FirstOrDefault(a => !string.IsNullOrEmpty(a.TitleName))?.TitleName ?? string.Empty;
        }

        return new TitleAchievements(
            TitleId: titleId,
            TitleName: name,
            EarnedGamerscore: earnedGamerscore,
            TotalGamerscore: metadata?.TotalGamerscore is > 0 ? metadata.TotalGamerscore.Value : totalGamerscore,
            EarnedCount: earnedCount,
            TotalCount: metadata?.TotalAchievements is > 0 ? metadata.TotalAchievements.Value : achievements.Count,
            // The title hub knows when the game was last launched. Failing that, the most
            // recent unlock is the best evidence we have -- and it is a lower bound, not a
            // guess, which is the distinction that keeps it honest.
            LastPlayed: metadata?.LastPlayed ?? latestUnlock,
            Achievements: achievements);
    }

    /// <summary>Map the title hub response into per-title metadata, keyed by title id.</summary>
    public static IReadOnlyDictionary<string, TitleMetadata> MapTitles(string json)
    {
        var parsed = Deserialize<TitleHubResponse>(json);
        var titles = new Dictionary<string, TitleMetadata>(StringComparer.Ordinal);

        if (parsed?.Titles is null)
        {
            return titles;
        }

        foreach (var title in parsed.Titles)
        {
            if (string.IsNullOrEmpty(title.TitleId))
            {
                continue;
            }

            titles[title.TitleId] = new TitleMetadata(
                TitleId: title.TitleId,
                Name: title.Name ?? string.Empty,
                LastPlayed: XboxJson.ParseTimestamp(title.TitleHistory?.LastTimePlayed),
                DisplayImage: title.DisplayImage,
                TotalGamerscore: title.Achievement?.TotalGamerscore,
                TotalAchievements: title.Achievement?.TotalAchievements);
        }

        return titles;
    }

    /// <summary>
    /// Map the profile settings response. The settings arrive as an unordered id/value
    /// list rather than named fields, so everything is a lookup.
    /// </summary>
    public static XboxProfile? MapProfile(string json)
    {
        var parsed = Deserialize<ProfileResponse>(json);
        var user = parsed?.ProfileUsers?.FirstOrDefault();

        if (user?.Id is null)
        {
            return null;
        }

        string? Setting(string id) => user.Settings?
            .FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase))?
            .Value;

        return new XboxProfile(
            Xuid: user.Id,
            Gamertag: Setting("Gamertag") ?? Setting("ModernGamertag") ?? string.Empty,
            Gamerscore: (int)XboxJson.ParseNumber(Setting("Gamerscore")),
            IconUrl: Setting("GameDisplayPicRaw"));
    }

    /// <summary>The continuation token for the next page, or null when this was the last one.</summary>
    public static string? ContinuationToken(string json) =>
        Deserialize<AchievementsResponse>(json)?.PagingInfo?.ContinuationToken is { Length: > 0 } token
            ? token
            : null;

    // -----------------------------------------------------------------------------------

    /// <summary>
    /// Gamerscore, summed across the reward entries that are actually gamerscore. Titles
    /// with cosmetic rewards list those alongside, with types like "Art" and "InApp".
    /// </summary>
    private static int Gamerscore(AchievementWire wire)
    {
        if (wire.Rewards is null)
        {
            return 0;
        }

        var total = 0d;
        foreach (var reward in wire.Rewards)
        {
            if (string.Equals(reward.Type, "Gamerscore", StringComparison.OrdinalIgnoreCase))
            {
                total += XboxJson.ParseNumber(reward.Value);
            }
        }

        return (int)Math.Round(total, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// How far through an achievement the player is, as a percentage.
    ///
    /// An unlocked achievement is 100 by definition and never computed, because a couple of
    /// titles ship requirements whose "current" stops short of "target" even once achieved.
    /// A multi-requirement achievement averages its parts, which is what the Xbox app does.
    /// </summary>
    private static double Progress(AchievementWire wire, bool unlocked)
    {
        if (unlocked)
        {
            return 100;
        }

        var requirements = wire.Progression?.Requirements;
        if (requirements is null || requirements.Count == 0)
        {
            return 0;
        }

        var sum = 0d;
        var counted = 0;

        foreach (var requirement in requirements)
        {
            var target = XboxJson.ParseNumber(requirement.Target);
            if (target <= 0)
            {
                continue;
            }

            sum += Math.Clamp(XboxJson.ParseNumber(requirement.Current) / target, 0, 1);
            counted++;
        }

        return counted == 0 ? 0 : Math.Round(sum / counted * 100, 2);
    }

    /// <summary>
    /// The text worth showing. Once unlocked, the description says what you did; while
    /// locked, "lockedDescription" says what to do -- except for secret achievements, where
    /// it is deliberately blank and the description is the only thing there is.
    /// </summary>
    private static string ChooseDescription(AchievementWire wire, bool unlocked)
    {
        if (unlocked)
        {
            return wire.Description ?? wire.LockedDescription ?? string.Empty;
        }

        return string.IsNullOrWhiteSpace(wire.LockedDescription)
            ? wire.Description ?? string.Empty
            : wire.LockedDescription;
    }

    private static string? IconUrl(AchievementWire wire)
    {
        if (wire.MediaAssets is null)
        {
            return null;
        }

        foreach (var asset in wire.MediaAssets)
        {
            if (string.Equals(asset.Type, "Icon", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(asset.Url))
            {
                return asset.Url;
            }
        }

        return null;
    }

    private static T? Deserialize<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            // A 200 with nothing in it. Not an argument bug on the caller's part -- the
            // service really does this occasionally -- so it gets the same actionable
            // exception as any other unusable response, rather than an ArgumentException
            // naming a parameter the operator has never heard of.
            throw new TrackerException(
                "Xbox returned an empty response where a document was expected.",
                "Retry once; an empty body from these endpoints is normally transient.");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, XboxJson.Read);
        }
        catch (JsonException ex)
        {
            throw new TrackerException(
                "Xbox returned a response this tool could not parse.",
                "If this is a fixture, it has drifted from the API shape it is meant to mimic.",
                ex);
        }
    }
}
