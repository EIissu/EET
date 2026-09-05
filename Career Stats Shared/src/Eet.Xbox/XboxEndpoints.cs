namespace Eet.Xbox;

/// <summary>
/// Every URL the Xbox token chain and the achievements client touch, in one place.
///
/// Two of these were checked against the live capture in
/// <c>shared/halo-endpoint-manifest.json</c> rather than taken on trust:
/// <c>settings.svc.halowaypoint.com/spartan-token</c> is the manifest's
/// <c>Settings_SpartanTokenV4</c> endpoint (authority <c>settings_noauth</c>, path
/// <c>/spartan-token</c> -- note it carries no Xbox authorization header of its own, the
/// XSTS token travels in the body), and <c>profile.xboxlive.com</c> is the manifest's
/// <c>xbl-profile</c> authority.
/// </summary>
public static class XboxEndpoints
{
    // ---- Step 1: Azure AD / Microsoft identity platform -------------------------------

    /// <summary>
    /// Device code flow, not authorization code. This is a desktop tool with nowhere to
    /// host a redirect URI, and the device code grant is the only public-client flow that
    /// does not need one. The tenant is <c>consumers</c> because Xbox accounts are personal
    /// Microsoft accounts, never work or school accounts.
    /// </summary>
    public const string DeviceCodeFormat = "https://login.microsoftonline.com/{0}/oauth2/v2.0/devicecode";

    public const string TokenFormat = "https://login.microsoftonline.com/{0}/oauth2/v2.0/token";

    /// <summary>
    /// <c>XboxLive.signin</c> is what the Xbox user authentication step will accept;
    /// <c>XboxLive.offline_access</c> is what makes Azure AD return a refresh token, and
    /// without it the user re-authenticates in a browser every hour.
    /// </summary>
    public const string DefaultScope = "XboxLive.signin XboxLive.offline_access";

    // ---- Step 2 and 3: Xbox Live ------------------------------------------------------

    public const string UserAuthenticate = "https://user.auth.xboxlive.com/user/authenticate";

    public const string XstsAuthorize = "https://xsts.auth.xboxlive.com/xsts/authorize";

    // ---- Step 4: Halo -----------------------------------------------------------------

    public const string SpartanToken = "https://settings.svc.halowaypoint.com/spartan-token";

    // ---- Services that ride on the XSTS token -----------------------------------------

    public const string Achievements = "https://achievements.xboxlive.com";

    public const string Profile = "https://profile.xboxlive.com";

    public const string TitleHub = "https://titlehub.xboxlive.com";
}

/// <summary>Known Xbox title ids.</summary>
public static class XboxTitles
{
    /// <summary>
    /// Halo Infinite. Taken from the game's own user agent -- the retail client identifies
    /// itself as <c>SHIVA-2043073184/...</c>, and that number is the title id achievements
    /// are filed under. It is deliberately not hard-coded anywhere but here, because it is
    /// the one value in this file the endpoint manifest does not corroborate.
    /// </summary>
    public const string HaloInfinite = "2043073184";
}
