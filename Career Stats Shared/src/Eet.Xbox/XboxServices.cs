using Eet.Trackers.Core;

namespace Eet.Xbox;

/// <summary>
/// The one decision that makes "runs today with no API keys" true: credentials present
/// means live, credentials absent means fixtures, and nothing above this line has to care
/// which it got.
///
/// The switch is on <see cref="XboxOptions.HasCredentials"/> alone -- the presence of an
/// Azure AD client id. It is not a debug flag, an environment name or a build
/// configuration, because all three have a way of being wrong in exactly the situation
/// where being wrong is worst: a release build that quietly serves synthetic numbers, or a
/// first run that demands an app registration the owner has not made yet.
/// </summary>
public static class XboxServices
{
    /// <summary>
    /// Build the token chain. Returns <see cref="FixtureXboxAuth"/> when there is no client
    /// id configured -- which is the default state, and is fine.
    /// </summary>
    public static IXboxAuth CreateAuth(
        XboxOptions options,
        HttpClient? http = null,
        IRefreshTokenStore? store = null,
        IDeviceCodePrompt? prompt = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.HasCredentials
            ? new XboxAuth(http ?? new HttpClient(), options, store, prompt, clock)
            : new FixtureXboxAuth(clock);
    }

    /// <summary>
    /// Build the achievements provider to match. A fixture auth always pairs with fixture
    /// achievements: half-live is a state nothing here should be able to reach.
    /// </summary>
    public static IXboxAchievements CreateAchievements(
        XboxOptions options,
        IXboxAuth auth,
        HttpClient? http = null,
        FixtureStore? fixtures = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(auth);

        if (!options.HasCredentials || auth is FixtureXboxAuth)
        {
            return new FixtureXboxAchievements(fixtures);
        }

        return new XboxAchievementsClient(http ?? new HttpClient(), auth, options);
    }

    /// <summary>
    /// A one-line, honest description of where the data came from, for the "Source" field
    /// on <see cref="CareerSnapshot"/> and for a badge on the dashboard. A user looking at
    /// synthetic numbers should never have to wonder whether they are real.
    /// </summary>
    public static string DescribeSource(IXboxAchievements achievements)
    {
        ArgumentNullException.ThrowIfNull(achievements);

        return achievements.IsFixture
            ? "Synthetic fixtures (no Xbox credentials configured)"
            : "Xbox Live (achievements.xboxlive.com)";
    }

    /// <summary>
    /// What to warn the user about, given the current configuration. Goes straight into
    /// <c>CareerSnapshot.Warnings</c>.
    /// </summary>
    public static IReadOnlyList<string> Warnings(XboxOptions options, IXboxAchievements achievements)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(achievements);

        var warnings = new List<string>(1);

        if (achievements.IsFixture)
        {
            warnings.Add(
                "Showing synthetic fixture data, not this account's real achievements. " +
                "Set EET_XBOX_CLIENT_ID to an Azure AD public-client application id to switch to live data.");
        }

        return warnings;
    }
}
