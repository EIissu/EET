using System.Text.Json.Serialization;

namespace Eet.Destiny.Client;

// Only the slices of Bungie's contract this tracker actually reads. Names and casing match
// the published OpenAPI document at https://github.com/Bungie-net/api so that a reader can
// diff them against the spec; anything not listed here is ignored on deserialization.

/// <summary>Bungie's platform ids. <c>All</c> is only valid on the player search.</summary>
public static class BungieMembershipType
{
    public const int None = 0;
    public const int Xbox = 1;
    public const int Psn = 2;
    public const int Steam = 3;
    public const int Blizzard = 4;
    public const int Stadia = 5;
    public const int Epic = 6;
    public const int Demon = 10;
    public const int BungieNext = 254;
    public const int All = -1;

    /// <summary>
    /// A display name for the platform. This doubles as the round-trip channel for the
    /// membership type: <see cref="Player"/> in the shared contract has exactly one id slot
    /// and Bungie keys on a (type, id) pair, so the type rides in Platform.
    /// </summary>
    public static string Name(int membershipType) => membershipType switch
    {
        Xbox => "Xbox",
        Psn => "PlayStation",
        Steam => "Steam",
        Blizzard => "Blizzard",
        Stadia => "Stadia",
        Epic => "Epic Games",
        Demon => "Demon",
        BungieNext => "Bungie.net",
        All => "All",
        _ => "Unknown",
    };

    /// <summary>The inverse of <see cref="Name"/>, plus the numeric forms.</summary>
    public static bool TryParse(string? value, out int membershipType)
    {
        membershipType = None;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (int.TryParse(trimmed, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var numeric))
        {
            membershipType = numeric;
            return true;
        }

        membershipType = trimmed.ToLowerInvariant() switch
        {
            "xbox" or "tigerxbox" => Xbox,
            "psn" or "playstation" or "tigerpsn" => Psn,
            "steam" or "tigersteam" or "pc" => Steam,
            "blizzard" or "tigerblizzard" => Blizzard,
            "stadia" or "tigerstadia" => Stadia,
            "epic" or "epicgames" or "epic games" or "tigeregs" => Epic,
            "demon" or "tigerdemon" => Demon,
            "bungie" or "bungie.net" or "bungienext" => BungieNext,
            "all" => All,
            _ => None,
        };

        return membershipType != None;
    }
}

/// <summary>The body of <c>SearchDestinyPlayerByBungieName</c>. Both halves are required.</summary>
public sealed class ExactSearchRequest
{
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>The four digits after the hash. Bungie types this as int16.</summary>
    [JsonPropertyName("displayNameCode")]
    public short DisplayNameCode { get; set; }
}

/// <summary>One membership. The search returns an array of these.</summary>
public sealed class UserInfoCard
{
    public long MembershipId { get; set; }

    public int MembershipType { get; set; }

    /// <summary>
    /// Non-zero when Cross Save is on, and then this is the membership every other endpoint
    /// wants. Querying the overridden platform instead returns a stale or empty profile.
    /// </summary>
    public int CrossSaveOverride { get; set; }

    public int[]? ApplicableMembershipTypes { get; set; }

    public bool IsPublic { get; set; }

    /// <summary>Platform display name, which is not the Bungie name.</summary>
    public string? DisplayName { get; set; }

    public string? BungieGlobalDisplayName { get; set; }

    public short BungieGlobalDisplayNameCode { get; set; }

    public string? IconPath { get; set; }

    /// <summary>The membership id Destiny keys on once Cross Save is taken into account.</summary>
    public int EffectiveMembershipType =>
        CrossSaveOverride == 0 ? MembershipType : CrossSaveOverride;

    /// <summary>"Guardian#1234", or the platform name when there is no Bungie name.</summary>
    public string Handle => string.IsNullOrWhiteSpace(BungieGlobalDisplayName)
        ? DisplayName ?? string.Empty
        : $"{BungieGlobalDisplayName}#{BungieGlobalDisplayNameCode:0000}";
}

/// <summary>Payload of <c>GetMembershipsById</c>.</summary>
public sealed class UserMembershipData
{
    public List<UserInfoCard>? DestinyMemberships { get; set; }

    public long? PrimaryMembershipId { get; set; }
}

// ---------------------------------------------------------------------------------------
// Profile
// ---------------------------------------------------------------------------------------

/// <summary>
/// Components arrive individually wrapped, each with its own privacy verdict. A component
/// can come back present-but-empty with <c>privacy</c> set to Private, which is a very
/// different thing from a player with no data.
/// </summary>
public sealed class ComponentResponse<T>
{
    public T? Data { get; set; }

    /// <summary>1 = Public, 2 = Private.</summary>
    public int Privacy { get; set; }

    public bool Disabled { get; set; }

    public bool IsPrivate => Privacy == 2;
}

public sealed class DestinyProfileResponse
{
    public ComponentResponse<DestinyProfileComponent>? Profile { get; set; }

    public ComponentResponse<Dictionary<string, DestinyCharacterComponent>>? Characters { get; set; }

    /// <summary>Requested by the brief; not mapped yet. See BungieOptions.ProfileComponents.</summary>
    public ComponentResponse<object>? ProfileRecords { get; set; }

    /// <summary>Requested by the brief; not mapped yet. See BungieOptions.ProfileComponents.</summary>
    public ComponentResponse<object>? Metrics { get; set; }
}

public sealed class DestinyProfileComponent
{
    public UserInfoCard? UserInfo { get; set; }

    public DateTimeOffset? DateLastPlayed { get; set; }

    /// <summary>Character ids as strings, because they are int64 and JavaScript exists.</summary>
    public List<string>? CharacterIds { get; set; }

    public int CurrentGuardianRank { get; set; }

    public int LifetimeHighestGuardianRank { get; set; }
}

public sealed class DestinyCharacterComponent
{
    public string? CharacterId { get; set; }

    public DateTimeOffset? DateLastPlayed { get; set; }

    public long MinutesPlayedTotal { get; set; }

    public int Light { get; set; }

    public int ClassType { get; set; }

    public string? EmblemPath { get; set; }

    public string? EmblemBackgroundPath { get; set; }

    /// <summary>0 Titan, 1 Hunter, 2 Warlock, 3 Unknown.</summary>
    public string ClassName => ClassType switch
    {
        0 => "Titan",
        1 => "Hunter",
        2 => "Warlock",
        _ => "Guardian",
    };
}

// ---------------------------------------------------------------------------------------
// Historical stats
// ---------------------------------------------------------------------------------------

/// <summary>
/// One stat. <c>basic</c> carries the raw number and Bungie's own formatting of it; the
/// display string is the one place Bungie has already decided how many decimals a stat
/// deserves, so it is worth keeping.
/// </summary>
public sealed class HistoricalStatsValue
{
    public string? StatId { get; set; }

    public HistoricalStatsValuePair? Basic { get; set; }

    /// <summary>Per-game-average. Present on all-time stats, absent on a single activity.</summary>
    public HistoricalStatsValuePair? Pga { get; set; }

    public HistoricalStatsValuePair? Weighted { get; set; }
}

public sealed class HistoricalStatsValuePair
{
    public double Value { get; set; }

    public string? DisplayValue { get; set; }
}

/// <summary>
/// The per-mode result of <c>GetHistoricalStats</c>. The endpoint returns a dictionary
/// keyed by mode name -- "allPvP", "allPvE", "control" -- not an array.
/// </summary>
public sealed class HistoricalStatsByPeriod
{
    public Dictionary<string, HistoricalStatsValue>? AllTime { get; set; }

    public List<HistoricalStatsPeriodGroup>? Daily { get; set; }

    public List<HistoricalStatsPeriodGroup>? Monthly { get; set; }
}

public sealed class HistoricalStatsPeriodGroup
{
    public DateTimeOffset Period { get; set; }

    public HistoricalStatsActivity? ActivityDetails { get; set; }

    public Dictionary<string, HistoricalStatsValue>? Values { get; set; }
}

public sealed class HistoricalStatsActivity
{
    /// <summary>Hash into DestinyActivityDefinition. For Crucible this names the map.</summary>
    public uint ReferenceId { get; set; }

    /// <summary>
    /// The playlist the player queued into, as opposed to the specific activity they landed
    /// in. Often equal to <see cref="ReferenceId"/>; when it differs it is the more useful
    /// label for "what were you playing".
    /// </summary>
    public uint DirectorActivityHash { get; set; }

    /// <summary>The PGCR id. int64, so it travels as a string.</summary>
    public string? InstanceId { get; set; }

    public int Mode { get; set; }

    /// <summary>
    /// Every mode this activity counts as, broadest first-ish. Bungie does not promise an
    /// order, so pick the most specific rather than the first.
    /// </summary>
    public int[]? Modes { get; set; }

    public bool IsPrivate { get; set; }

    public int MembershipType { get; set; }
}

public sealed class ActivityHistoryResults
{
    public List<HistoricalStatsPeriodGroup>? Activities { get; set; }
}

// ---------------------------------------------------------------------------------------
// Post game carnage report
// ---------------------------------------------------------------------------------------

public sealed class PostGameCarnageReport
{
    public DateTimeOffset Period { get; set; }

    public HistoricalStatsActivity? ActivityDetails { get; set; }

    public List<PostGameCarnageReportEntry>? Entries { get; set; }

    public List<PostGameCarnageReportTeamEntry>? Teams { get; set; }
}

public sealed class PostGameCarnageReportEntry
{
    public int Standing { get; set; }

    public string? CharacterId { get; set; }

    public DestinyPlayerInfo? Player { get; set; }

    public Dictionary<string, HistoricalStatsValue>? Values { get; set; }

    public PostGameCarnageReportExtendedData? Extended { get; set; }
}

/// <summary>
/// The only place Bungie publishes anything accuracy-shaped: <c>precisionKills</c> and the
/// per-weapon <c>uniqueWeaponKillsPrecisionKills</c> ratio. There is no shots-fired or
/// shots-hit stat anywhere in the Destiny API.
/// </summary>
public sealed class PostGameCarnageReportExtendedData
{
    public Dictionary<string, HistoricalStatsValue>? Values { get; set; }

    public List<HistoricalWeaponStats>? Weapons { get; set; }
}

public sealed class HistoricalWeaponStats
{
    public uint ReferenceId { get; set; }

    public Dictionary<string, HistoricalStatsValue>? Values { get; set; }
}

public sealed class PostGameCarnageReportTeamEntry
{
    public int TeamId { get; set; }

    public HistoricalStatsValue? Standing { get; set; }

    public HistoricalStatsValue? Score { get; set; }

    public string? TeamName { get; set; }
}

public sealed class DestinyPlayerInfo
{
    public UserInfoCard? DestinyUserInfo { get; set; }

    public string? CharacterClass { get; set; }

    public int CharacterLevel { get; set; }

    public int LightLevel { get; set; }
}

// ---------------------------------------------------------------------------------------
// Manifest
// ---------------------------------------------------------------------------------------

public sealed class DestinyManifest
{
    /// <summary>
    /// The cache key. It changes on every content deploy, and it is the only signal that a
    /// cached definition table has gone stale.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Locale, then definition table name, then a site-root relative path. Fetching only
    /// the two tables this tracker needs is the difference between a few hundred kilobytes
    /// and the hundred-megabyte world SQLite file.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>>? JsonWorldComponentContentPaths { get; set; }

    /// <summary>The whole-world JSON blob. Present, and deliberately not used.</summary>
    public Dictionary<string, string>? JsonWorldContentPaths { get; set; }
}

/// <summary>A row of DestinyActivityDefinition, trimmed to what a career page shows.</summary>
public sealed class ActivityDefinition
{
    public DisplayPropertiesDefinition? DisplayProperties { get; set; }

    public DisplayPropertiesDefinition? OriginalDisplayProperties { get; set; }

    public DisplayPropertiesDefinition? SelectionScreenDisplayProperties { get; set; }

    public uint Hash { get; set; }

    public bool IsPvP { get; set; }

    public bool IsPlaylist { get; set; }

    public int DirectActivityModeType { get; set; }

    public int[]? ActivityModeTypes { get; set; }

    public string? PgcrImage { get; set; }
}

/// <summary>A row of DestinyActivityModeDefinition.</summary>
public sealed class ActivityModeDefinition
{
    public DisplayPropertiesDefinition? DisplayProperties { get; set; }

    public int ModeType { get; set; }

    /// <summary>0 None, 1 PvE, 2 PvP, 3 PvECompetitive.</summary>
    public int ActivityModeCategory { get; set; }

    public bool IsTeamBased { get; set; }

    /// <summary>
    /// True for umbrella modes such as AllPvP. Never a useful label for a single match --
    /// "All PvP" tells a player nothing they did not already know.
    /// </summary>
    public bool IsAggregateMode { get; set; }

    public uint Hash { get; set; }
}

public sealed class DisplayPropertiesDefinition
{
    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? Icon { get; set; }

    public bool HasIcon { get; set; }
}
