using Eet.Trackers.Core;

namespace Eet.Xbox;

/// <summary>
/// Everything the token chain needs that is not a secret we generate ourselves.
///
/// <see cref="ClientId"/> is the only value the owner must supply, and it is not a secret:
/// an Azure AD public client id is published in the redirect of every desktop app that
/// uses one. There is deliberately no client secret anywhere in this type -- the device
/// code flow is a public client flow and adding a secret would break it.
/// </summary>
public sealed record XboxOptions
{
    /// <summary>
    /// The application (client) id of an Azure AD app registration the owner controls.
    /// The registration must be a public client with "Allow public client flows" enabled,
    /// and must have the delegated <c>XboxLive.signin</c> permission.
    /// Null or blank means "no credentials", and callers should serve fixtures instead.
    /// </summary>
    public string? ClientId { get; init; }

    /// <summary>
    /// Xbox accounts are personal Microsoft accounts, so the tenant is <c>consumers</c>.
    /// Overridable only because a test needs to point it somewhere harmless.
    /// </summary>
    public string Tenant { get; init; } = "consumers";

    public string Scope { get; init; } = XboxEndpoints.DefaultScope;

    /// <summary>
    /// <c>RETAIL</c> unless you are 343 Industries. A wrong sandbox id is one of the ways
    /// XSTS returns a 401 with no useful message.
    /// </summary>
    public string SandboxId { get; init; } = "RETAIL";

    /// <summary>
    /// Where the refresh token is cached. Defaults to
    /// <c>%LOCALAPPDATA%/eet-trackers/xbox-refresh-token.tokencache.json</c> (and the XDG
    /// equivalent elsewhere). This file is a live credential: see
    /// <see cref="RefreshTokenStore"/>. Point it wherever you like, but keep the
    /// <c>.tokencache.json</c> suffix -- that is what the repository .gitignore matches on,
    /// and it is the only thing standing between a working-tree path and a committed
    /// credential.
    /// </summary>
    public string? TokenCachePath { get; init; }

    /// <summary>
    /// Sent on the Halo-facing calls. The Spartan token endpoint is noticeably happier
    /// with a user agent than without one.
    /// </summary>
    public string UserAgent { get; init; } = "EetTrackers/1.0 (+https://github.com/eet/trackers)";

    /// <summary>
    /// <c>Accept-Language</c> for the title hub, which localises game names by it.
    /// Invariant by default so a French Windows install and an English one produce the
    /// same fixture-comparable output.
    /// </summary>
    public string AcceptLanguage { get; init; } = "en-US, en";

    /// <summary>
    /// Directory holding the raw API-shaped fixtures. Defaults to a search that walks up
    /// from the build output looking for <c>Career Stats Shared/fixtures</c>, then falls back
    /// to the copies embedded in this assembly.
    /// </summary>
    public string? FixtureDirectory { get; init; }

    /// <summary>True when there is no client id and the only honest thing to do is serve fixtures.</summary>
    public bool HasCredentials => !string.IsNullOrWhiteSpace(ClientId);

    /// <summary>
    /// Read the options from the environment, which is how the owner supplies them without
    /// a config file. Nothing here reads a secret; a client id is public by design.
    /// </summary>
    public static XboxOptions FromEnvironment()
    {
        var clientId = Environment.GetEnvironmentVariable("EET_XBOX_CLIENT_ID");
        var tenant = Environment.GetEnvironmentVariable("EET_XBOX_TENANT");
        var fixtures = Environment.GetEnvironmentVariable("EET_FIXTURES_DIR");
        var cache = Environment.GetEnvironmentVariable("EET_XBOX_TOKEN_CACHE");

        return new XboxOptions
        {
            ClientId = string.IsNullOrWhiteSpace(clientId) ? null : clientId.Trim(),
            Tenant = string.IsNullOrWhiteSpace(tenant) ? "consumers" : tenant.Trim(),
            FixtureDirectory = string.IsNullOrWhiteSpace(fixtures) ? null : fixtures,
            TokenCachePath = string.IsNullOrWhiteSpace(cache) ? null : cache,
        };
    }

    /// <summary>Throw a message the operator can act on, rather than a null reference later.</summary>
    public string RequireClientId() =>
        string.IsNullOrWhiteSpace(ClientId)
            ? throw new TrackerException(
                "No Azure AD client id is configured, so the Xbox token chain cannot start.",
                "Register a public-client Azure AD application with the delegated XboxLive.signin " +
                "permission and set EET_XBOX_CLIENT_ID to its application (client) id. " +
                "Until then, run with the fixture sources -- they need no credentials at all.")
            : ClientId;
}
