namespace Eet.Trackers.Core;

/// <summary>
/// Anything that can produce a career snapshot for a player.
///
/// Both games implement this, which is the whole reason a single dashboard can render
/// either one. The mapping from a game's own vocabulary into
/// <see cref="CareerSnapshot"/> lives in that game's client, never in the UI.
/// </summary>
public interface ICareerSource
{
    GameId Game { get; }

    /// <summary>True when this source is serving recorded fixtures rather than live data.</summary>
    bool IsFixture { get; }

    /// <summary>
    /// Resolve a free-text query -- a gamertag, an XUID, a Bungie name -- to a player.
    /// Returns null when nothing matches.
    /// </summary>
    Task<Player?> ResolveAsync(string query, CancellationToken ct = default);

    Task<CareerSnapshot> GetSnapshotAsync(Player player, CancellationToken ct = default);
}

/// <summary>
/// The Xbox Live token chain.
///
/// Three exchanges stand between an Azure AD sign-in and a usable Xbox request:
///
///   1. Azure AD gives an OAuth access token for the XboxLive.signin scope.
///   2. <c>user.auth.xboxlive.com/user/authenticate</c> turns that into a user token.
///   3. <c>xsts.auth.xboxlive.com/xsts/authorize</c> turns the user token into an XSTS
///      token for one relying party.
///
/// Step 3 is the interesting one: the relying party decides which service will accept the
/// result. Ask for <see cref="RelyingParty.XboxLive"/> and the token opens
/// achievements.xboxlive.com; ask for <see cref="RelyingParty.Halo"/> and it can be traded
/// for a Spartan token. Same first two steps either way, which is why achievements come
/// almost free once Halo works.
/// </summary>
public interface IXboxAuth
{
    /// <summary>An XSTS token plus the user hash that must accompany it.</summary>
    Task<XstsToken> GetXstsTokenAsync(string relyingParty, CancellationToken ct = default);

    /// <summary>
    /// The Halo-specific token, obtained by trading a Halo-audience XSTS token at the
    /// Halo Waypoint token endpoint. Short-lived; implementations should cache and renew.
    /// </summary>
    Task<SpartanToken> GetSpartanTokenAsync(CancellationToken ct = default);
}

/// <summary>Known relying parties for step 3 of the chain.</summary>
public static class RelyingParty
{
    /// <summary>Opens the general Xbox Live services, including achievements and profile.</summary>
    public const string XboxLive = "http://xboxlive.com";

    /// <summary>The audience Halo Infinite's own token endpoint expects.</summary>
    public const string Halo = "https://prod.xsts.halowaypoint.com/";
}

/// <param name="Token">The XSTS token itself.</param>
/// <param name="UserHash">
/// The <c>uhs</c> claim. Both halves are required: the Authorization header is
/// <c>XBL3.0 x={UserHash};{Token}</c> and a request with only the token is rejected.
/// </param>
public sealed record XstsToken(string Token, string UserHash, DateTimeOffset ExpiresAt, string? Xuid)
{
    public string AuthorizationHeader => $"XBL3.0 x={UserHash};{Token}";
    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt - TimeSpan.FromMinutes(2);
}

/// <param name="Token">Goes in the <c>X-343-Authorization-Spartan</c> header.</param>
public sealed record SpartanToken(string Token, DateTimeOffset ExpiresAt)
{
    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt - TimeSpan.FromMinutes(2);
}

/// <summary>An unlocked or in-progress achievement, normalised across titles.</summary>
public sealed record Achievement(
    string Id,
    string TitleId,
    string TitleName,
    string Name,
    string Description,
    int Gamerscore,
    bool Unlocked,
    double ProgressPercent,
    DateTimeOffset? UnlockedAt,
    bool IsRare,
    double? RarityPercent,
    string? IconUrl);

/// <summary>A game's achievement standing for one player.</summary>
public sealed record TitleAchievements(
    string TitleId,
    string TitleName,
    int EarnedGamerscore,
    int TotalGamerscore,
    int EarnedCount,
    int TotalCount,
    DateTimeOffset? LastPlayed,
    IReadOnlyList<Achievement> Achievements)
{
    public double Completion => TotalGamerscore == 0 ? 0 : (double)EarnedGamerscore / TotalGamerscore;
}

/// <summary>
/// Raised when a game's API refuses a request in a way the user can act on -- a missing
/// key, an expired token, a private profile. Distinct from a transport failure.
/// </summary>
public sealed class TrackerException : Exception
{
    public TrackerException(string message, string? remedy = null, Exception? inner = null)
        : base(message, inner) => Remedy = remedy;

    /// <summary>What the operator should do about it, in plain language.</summary>
    public string? Remedy { get; }
}
