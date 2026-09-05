using System.Globalization;
using Eet.Trackers.Core;

namespace Eet.Xbox;

/// <summary>
/// The token chain, without the tokens.
///
/// This exists so the entire application runs with no Azure app registration, no Microsoft
/// account and no network -- which is the state the owner is in today, and the state every
/// test should be in always. It hands back structurally valid <see cref="XstsToken"/> and
/// <see cref="SpartanToken"/> values so code downstream can build its Authorization headers
/// normally, and those headers are never sent anywhere: the fixture achievements provider
/// reads JSON off disk.
///
/// The token strings are deliberately, visibly fake. They are not random-looking strings
/// that might be mistaken for a leaked credential in a log or a screenshot, and they are
/// not real-shaped JWTs. If one of these ever turns up in a bug report, it says plainly
/// what it is.
/// </summary>
public sealed class FixtureXboxAuth : IXboxAuth
{
    /// <summary>The XUID the fixtures are recorded against.</summary>
    public const string FixtureXuid = "2814648798129555";

    /// <summary>
    /// The gamertag in the fixtures. Note the first character is U+0406 CYRILLIC CAPITAL
    /// LETTER BYELORUSSIAN-UKRAINIAN I, not a Latin I -- the fixture deliberately carries a
    /// homoglyph tag so the identity handling in Eet.Trackers.Core.Identity is exercised by
    /// the default, zero-credential path rather than only by a test nobody runs.
    /// </summary>
    public const string FixtureGamertag = "Іlissu";

    private readonly TimeProvider _clock;

    public FixtureXboxAuth(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public bool IsFixture => true;

    public Task<XstsToken> GetXstsTokenAsync(string relyingParty, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relyingParty);
        ct.ThrowIfCancellationRequested();

        // An hour out, so IsExpired is false and any renewal logic downstream stays quiet.
        return Task.FromResult(new XstsToken(
            Token: string.Create(CultureInfo.InvariantCulture, $"FIXTURE-XSTS-TOKEN-NOT-A-CREDENTIAL:{Label(relyingParty)}"),
            UserHash: "0000000000000000000",
            ExpiresAt: _clock.GetUtcNow().AddHours(1),
            Xuid: FixtureXuid));
    }

    public Task<SpartanToken> GetSpartanTokenAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(new SpartanToken(
            Token: "FIXTURE-SPARTAN-TOKEN-NOT-A-CREDENTIAL",
            ExpiresAt: _clock.GetUtcNow().AddHours(1)));
    }

    /// <summary>A short, readable tag for which relying party was asked for.</summary>
    private static string Label(string relyingParty) => relyingParty switch
    {
        RelyingParty.XboxLive => "xboxlive",
        RelyingParty.Halo => "halo",
        _ => "other",
    };
}
