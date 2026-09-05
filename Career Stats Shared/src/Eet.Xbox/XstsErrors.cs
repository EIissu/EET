using System.Globalization;
using System.Net;
using System.Text.Json;
using Eet.Trackers.Core;
using Eet.Xbox.Wire;

namespace Eet.Xbox;

/// <summary>
/// Turning Xbox's 401s into something a person can act on.
///
/// The Xbox authentication endpoints have an unhelpful habit: when they refuse the
/// ACCOUNT rather than the request, they still answer <c>401 Unauthorized</c>, and put the
/// actual reason in an <c>XErr</c> number in the body. A client that calls
/// <c>EnsureSuccessStatusCode</c> collapses five completely different, individually
/// fixable problems -- no Xbox profile, a child account, a banned account, an unsupported
/// country -- into one indistinguishable "401 Unauthorized", which is exactly the failure
/// mode that makes people think a tracker is broken when their account simply needs a
/// visit to xbox.com.
///
/// So every response from steps 2 and 3 goes through <see cref="EnsureAuthorizedAsync"/>
/// instead, and each known code gets a <see cref="TrackerException.Remedy"/>.
/// </summary>
public static class XstsErrors
{
    /// <summary>The Microsoft account has never signed in to Xbox, so it has no Xbox profile.</summary>
    public const long NoXboxAccount = 2148916233;

    /// <summary>Xbox Live is not offered in this account's country or region.</summary>
    public const long CountryNotAvailable = 2148916235;

    /// <summary>The account is a child account and is not in a Microsoft family group.</summary>
    public const long ChildAccount = 2148916238;

    /// <summary>The account is banned from Xbox Live.</summary>
    public const long AccountBanned = 2148916227;

    /// <summary>
    /// Throw a useful exception unless the response succeeded. Reads the body, because
    /// that is the only place the reason exists.
    /// </summary>
    public static async Task EnsureAuthorizedAsync(
        HttpResponseMessage response,
        string stage,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await ReadBodyAsync(response, ct).ConfigureAwait(false);
        throw Translate(response.StatusCode, body, stage);
    }

    /// <summary>
    /// The pure half of <see cref="EnsureAuthorizedAsync"/>: status plus body in, exception
    /// out. Separated so the mapping can be tested without an <see cref="HttpClient"/>.
    /// </summary>
    public static TrackerException Translate(HttpStatusCode status, string? body, string stage)
    {
        var code = ExtractXErr(body);

        if (code is not null)
        {
            var (message, remedy) = Describe(code.Value);
            return new TrackerException(
                string.Create(CultureInfo.InvariantCulture, $"{stage}: {message} (XErr {code.Value})"),
                remedy);
        }

        // No XErr means it is the request we got wrong, not the account.
        var remedyForStatus = status switch
        {
            HttpStatusCode.Unauthorized =>
                "The Xbox token was rejected without an XErr code, which usually means the token " +
                "expired between steps. Retry once; if it persists, delete the cached refresh token " +
                "and sign in again.",
            HttpStatusCode.Forbidden =>
                "Xbox accepted the identity but refused the operation. Check the sandbox id is RETAIL " +
                "and that the Azure AD app registration has the delegated XboxLive.signin permission.",
            HttpStatusCode.BadRequest =>
                "Xbox rejected the request body. The most common cause is a missing \"d=\" prefix on " +
                "the RpsTicket, which is required for Azure AD access tokens.",
            HttpStatusCode.TooManyRequests =>
                "Xbox is rate limiting. Wait a minute and retry; the achievements service is stricter " +
                "than the token endpoints.",
            _ =>
                "Xbox returned an unexpected status. This is a transport or service problem rather " +
                "than an account problem; retrying later is reasonable.",
        };

        return new TrackerException(
            string.Create(CultureInfo.InvariantCulture, $"{stage}: Xbox returned HTTP {(int)status} ({status})."),
            remedyForStatus);
    }

    /// <summary>The human-facing halves for a known code. Unknown codes get an honest shrug.</summary>
    private static (string Message, string Remedy) Describe(long code) => code switch
    {
        NoXboxAccount => (
            "this Microsoft account has no Xbox profile",
            "Sign in once at https://www.xbox.com with this Microsoft account to create an Xbox " +
            "profile, then run the sign-in again. Nothing else needs changing."),

        ChildAccount => (
            "this is a child account and needs an adult to authorise it",
            "An adult must add this account to a Microsoft family group at " +
            "https://account.microsoft.com/family and grant it Xbox Live access. The token chain " +
            "cannot proceed until then."),

        CountryNotAvailable => (
            "Xbox Live is not available in this account's country or region",
            "Xbox Live does not operate in the region on this Microsoft account. Change the " +
            "account's country at https://account.microsoft.com/profile if it is set wrongly, " +
            "otherwise this account cannot be used."),

        AccountBanned => (
            "this account is banned from Xbox Live",
            "Xbox Live has suspended this account. Check the enforcement history at " +
            "https://enforcement.xbox.com -- no change to this tool will get past it."),

        _ => (
            "Xbox refused the sign-in",
            string.Create(
                CultureInfo.InvariantCulture,
                $"Xbox returned XErr {code}, which this tool has no specific remedy for. Searching that number will usually turn up the account condition behind it.")),
    };

    /// <summary>
    /// Pull <c>XErr</c> out of the body. Tolerates a non-JSON body, an HTML error page, and
    /// the number arriving as a string, because all three happen.
    /// </summary>
    internal static long? ExtractXErr(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            var error = JsonSerializer.Deserialize<XstsErrorResponse>(body, XboxJson.Read);
            return error is { XErr: > 0 } ? error.XErr : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string?> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
