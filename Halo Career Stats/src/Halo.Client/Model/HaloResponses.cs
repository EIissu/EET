using System.Text.Json;
using System.Text.Json.Serialization;

namespace Eet.Halo.Client.Model;

/// <summary>
/// The raw response shapes, as close to what the wire actually carries as C# allows.
///
/// These are deliberately dumb: no computed properties beyond trivial lookups, no
/// normalisation, no opinions. Everything interesting happens in
/// <see cref="Mapping.HaloMapper"/>, which is what makes the fixtures worth having --
/// a fixture is raw API-shaped JSON, so loading one runs the same deserialisation and the
/// same mapping the live path runs.
///
/// PascalCase throughout, because that is what these services emit. The two exceptions
/// (Xbox profile, which is camelCase) are marked where they appear.
/// </summary>
public static class HaloJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new IsoDurationConverter());
        options.Converters.Add(new NullableIsoDurationConverter());
        return options;
    }
}

/// <summary>A reference to a UGC asset: a map, a game variant, a playlist.</summary>
public sealed record HaloAssetRef(int AssetKind, string? AssetId, string? VersionId);

/// <summary>
/// The header block that appears identically in match history and match stats.
/// </summary>
/// <param name="GameVariantCategory">
/// The mode family, as a numeric id. See <see cref="HaloEnums.GameVariantCategoryName"/>
/// for what the numbers mean and how confident we are about that.
/// </param>
public sealed record HaloMatchInfo(
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    [property: JsonConverter(typeof(NullableIsoDurationConverter))] TimeSpan? Duration,
    int LifecycleMode,
    int GameVariantCategory,
    string? LevelId,
    HaloAssetRef? MapVariant,
    HaloAssetRef? UgcGameVariant,
    string? ClearanceId,
    HaloAssetRef? Playlist,
    int PlaylistExperience,
    HaloAssetRef? PlaylistMapModePair,
    string? SeasonId,
    [property: JsonConverter(typeof(NullableIsoDurationConverter))] TimeSpan? PlayableDuration,
    bool TeamsEnabled,
    bool TeamScoringEnabled);

/// <summary>One row of <c>/hi/players/{player}/matches</c>.</summary>
public sealed record HaloMatchHistoryResult(
    string MatchId,
    HaloMatchInfo? MatchInfo,
    int LastTeamId,
    int Outcome,
    int Rank,
    bool PresentAtEndOfMatch);

/// <summary>The match history page itself.</summary>
public sealed record HaloMatchHistoryResponse(
    int Start,
    int Count,
    int ResultCount,
    IReadOnlyList<HaloMatchHistoryResult>? Results)
{
    public IReadOnlyList<HaloMatchHistoryResult> Matches => Results ?? [];
}

/// <summary>
/// <c>/hi/players/{player}/matches/count</c>. Cheap, and the only honest way to say
/// "you have played 1,412 games" without paging all of them.
/// </summary>
public sealed record HaloMatchCountResponse(
    int CustomMatchesPlayedCount,
    int MatchesPlayedCount,
    int MatchmadeMatchesPlayedCount,
    int LocalMatchesPlayedCount,
    int CustomLocalMatchesPlayedCount);

/// <summary>
/// The per-player numbers. Note <see cref="Accuracy"/>: this service reports it as a
/// percentage (46.875 means 46.875%), not a fraction. Mapping divides by 100 exactly once,
/// which is the sort of thing that is worth writing down because getting it wrong renders
/// a plausible-looking 4687% and nobody notices for a week.
/// </summary>
public sealed record HaloCoreStats(
    int Score,
    int PersonalScore,
    int RoundsWon,
    int RoundsLost,
    int RoundsTied,
    int Kills,
    int Deaths,
    int Assists,
    double KDA,
    int Suicides,
    int Betrayals,
    [property: JsonConverter(typeof(NullableIsoDurationConverter))] TimeSpan? AverageLifeDuration,
    int GrenadeKills,
    int HeadshotKills,
    int MeleeKills,
    int PowerWeaponKills,
    int ShotsFired,
    int ShotsHit,
    double Accuracy,
    int DamageDealt,
    int DamageTaken,
    int CalloutAssists,
    int VehicleDestroys,
    int DriverAssists,
    int Hijacks,
    int EmpAssists,
    int MaxKillingSpree,
    IReadOnlyList<HaloMedal>? Medals,
    int Spawns);

/// <summary>
/// A medal award. <see cref="NameId"/> is a hash into the game CMS medal metadata; without
/// that metadata it is a number, which is why medals are carried but not displayed.
/// </summary>
public sealed record HaloMedal(long NameId, int Count, int TotalPersonalScoreAwarded);

/// <summary>The mode-specific stat blocks. Only CoreStats is always present.</summary>
public sealed record HaloStatsBundle(HaloCoreStats? CoreStats);

public sealed record HaloTeamStats(int TeamId, int Outcome, int Rank, HaloStatsBundle? Stats);

public sealed record HaloParticipationInfo(
    DateTimeOffset? FirstJoinedTime,
    DateTimeOffset? LastLeaveTime,
    bool PresentAtBeginning,
    bool JoinedInProgress,
    bool LeftInProgress,
    bool PresentAtCompletion,
    [property: JsonConverter(typeof(NullableIsoDurationConverter))] TimeSpan? TimePlayed);

/// <summary>
/// One player in a match.
/// </summary>
/// <param name="PlayerId">
/// Always in <c>xuid(...)</c> form for humans. Bots use a <c>bid(...)</c> form instead,
/// which is how <see cref="PlayerType"/> 2 is spotted without trusting the enum.
/// </param>
/// <param name="PlayerTeamStats">
/// A list, not a single object, because a player who switches teams mid-match accrues
/// stats under each. Mapping sums across all of them rather than taking the first.
/// </param>
public sealed record HaloMatchPlayer(
    string? PlayerId,
    int PlayerType,
    int LastTeamId,
    int Outcome,
    int Rank,
    HaloParticipationInfo? ParticipationInfo,
    IReadOnlyList<HaloPlayerTeamStats>? PlayerTeamStats);

public sealed record HaloPlayerTeamStats(int TeamId, HaloStatsBundle? Stats);

/// <summary><c>/hi/matches/{matchId}/stats</c>.</summary>
public sealed record HaloMatchStatsResponse(
    string MatchId,
    HaloMatchInfo? MatchInfo,
    IReadOnlyList<HaloTeamStats>? Teams,
    IReadOnlyList<HaloMatchPlayer>? Players);

/// <summary>
/// A competitive skill rating.
/// </summary>
/// <param name="SubTier">
/// Zero-based on the wire and one-based in every UI that has ever shown it: SubTier 0 in
/// Diamond is displayed "Diamond 1". <see cref="HaloEnums.FormatRank"/> adds the one.
/// </param>
public sealed record HaloCsr(
    int Value,
    int MeasurementMatchesRemaining,
    string? Tier,
    int TierStart,
    int SubTier,
    string? NextTier,
    int NextTierStart,
    int NextSubTier,
    int InitialMeasurementMatches);

public sealed record HaloRankRecap(HaloCsr? PreMatchCsr, HaloCsr? PostMatchCsr);

/// <summary>
/// What the skill service thought the player should have done. Expected kills against
/// actual kills is the closest thing Halo has to a "did you outperform your rank" number.
/// </summary>
public sealed record HaloStatPerformance(double Count, double Expected, double StdDev);

public sealed record HaloMatchSkillResult(
    int TeamId,
    double TeamMmr,
    HaloRankRecap? RankRecap,
    IReadOnlyDictionary<string, HaloStatPerformance>? StatPerformances);

/// <summary>
/// The skill endpoints answer per player id, with a per-entry
/// <see cref="ResultCode"/>: 0 is success, and a non-zero code with a null
/// <see cref="Result"/> is how "this player has no rank in this playlist" arrives. It is
/// not an HTTP error and must not be treated as one.
/// </summary>
public sealed record HaloSkillEntry<T>(string? Id, int ResultCode, T? Result) where T : class;

public sealed record HaloSkillResponse<T>(IReadOnlyList<HaloSkillEntry<T>>? Value) where T : class
{
    public IReadOnlyList<HaloSkillEntry<T>> Entries => Value ?? [];

    public T? For(string playerId) => Entries
        .FirstOrDefault(e => e.ResultCode == 0
            && string.Equals(e.Id, playerId, StringComparison.OrdinalIgnoreCase))?.Result;
}

public sealed record HaloPlaylistCsrResult(HaloCsr? Current, HaloCsr? SeasonMax, HaloCsr? AllTimeMax);

/// <summary>
/// The Waypoint service record. Provenance is weaker than everything else in this file:
/// this endpoint is not in 343's published manifest, so the field list here is a best
/// reading of captured traffic and may be incomplete. Treat a null as "we did not see it",
/// not "the server omitted it".
/// </summary>
public sealed record HaloServiceRecordResponse(
    [property: JsonConverter(typeof(NullableIsoDurationConverter))] TimeSpan? TimePlayed,
    int MatchesCompleted,
    int Wins,
    int Losses,
    int Ties,
    int DidNotFinish,
    HaloCoreStats? CoreStats);

/// <summary>
/// A UGC asset as the discovery service returns it. Only <see cref="PublicName"/> is
/// wanted: it is the difference between a breakdown row reading "Live Fire" and one
/// reading "8420410b-044d-44d7-80b6-98a766c8c39f".
/// </summary>
public sealed record HaloUgcAsset(
    string? AssetId,
    string? VersionId,
    string? PublicName,
    string? Description);

/// <summary>
/// The Xbox profile response. camelCase, unlike everything else here, because it comes
/// from profile.xboxlive.com rather than from a Halo service.
/// </summary>
public sealed record XboxProfileResponse(IReadOnlyList<XboxProfileUser>? ProfileUsers)
{
    public IReadOnlyList<XboxProfileUser> Users => ProfileUsers ?? [];
}

public sealed record XboxProfileUser(string? Id, IReadOnlyList<XboxProfileSetting>? Settings)
{
    public string? Setting(string name) => Settings?
        .FirstOrDefault(s => string.Equals(s.Id, name, StringComparison.OrdinalIgnoreCase))?.Value;
}

public sealed record XboxProfileSetting(string? Id, string? Value);
