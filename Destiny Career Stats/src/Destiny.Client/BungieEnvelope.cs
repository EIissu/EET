using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eet.Trackers.Core;

namespace Eet.Destiny.Client;

/// <summary>
/// The wrapper every Bungie.net platform response arrives in.
///
/// The property names really are PascalCase here while everything inside
/// <see cref="Response"/> is camelCase, which is why the shared serializer options are
/// case-insensitive rather than carrying two naming policies.
/// </summary>
public sealed class BungieEnvelope<T>
{
    public T? Response { get; set; }

    /// <summary>
    /// A <see cref="BungiePlatformError"/> value. <c>1</c> is Success and nothing else is.
    /// </summary>
    public int ErrorCode { get; set; }

    public string? ErrorStatus { get; set; }

    public string? Message { get; set; }

    /// <summary>How long to wait before retrying, when the failure is a throttle.</summary>
    public int ThrottleSeconds { get; set; }

    public Dictionary<string, string>? MessageData { get; set; }

    public string? DetailedErrorTrace { get; set; }

    public bool IsSuccess => ErrorCode == BungiePlatformError.Success;
}

/// <summary>
/// Bungie's PlatformErrorCodes -- the handful a read-only career tracker can actually hit,
/// plus what an operator should do about each.
///
/// This exists because of the single most common mistake against this API: Bungie answers
/// HTTP 200 for almost everything, including "your key is invalid" and "that profile is
/// private". The status code tells you the request reached a server. ErrorCode tells you
/// whether it worked.
/// </summary>
public static class BungiePlatformError
{
    public const int Success = 1;
    public const int TransportException = 2;
    public const int UnhandledException = 3;
    public const int SystemDisabled = 5;
    public const int ParameterParseFailure = 7;
    public const int InvalidParameters = 18;
    public const int ParameterNotFound = 19;
    public const int NotFound = 21;

    public const int ThrottleLimitExceeded = 31;
    public const int ThrottleLimitExceededMinutes = 35;
    public const int ThrottleLimitExceededMomentarily = 36;
    public const int ThrottleLimitExceededSeconds = 37;
    public const int PerEndpointRequestThrottleExceeded = 51;
    public const int PerApplicationThrottleExceeded = 54;
    public const int PerApplicationAnonymousThrottleExceeded = 55;
    public const int PerApplicationAuthenticatedThrottleExceeded = 56;
    public const int PerUserThrottleExceeded = 57;

    public const int UserCannotResolveCentralAccount = 217;

    public const int DestinyAccountAcquisitionFailure = 1600;
    public const int DestinyAccountNotFound = 1601;
    public const int DestinyUnexpectedError = 1618;
    public const int DestinyCharacterNotFound = 1620;
    public const int DestinyInvalidMembershipType = 1630;
    public const int DestinyShardRelayClientTimeout = 1651;
    public const int DestinyPGCRNotFound = 1653;
    public const int DestinyPrivacyRestriction = 1665;
    public const int DestinyLegacyPlatformInaccessible = 1670;
    public const int DestinyThrottledByGameServer = 1672;
    public const int DestinyDirectBabelClientTimeout = 1688;

    public const int ApiExceededMaxKeys = 2100;
    public const int ApiInvalidOrExpiredKey = 2101;
    public const int ApiKeyMissingFromRequest = 2102;

    private static readonly HashSet<int> Throttles =
    [
        ThrottleLimitExceeded,
        ThrottleLimitExceededMinutes,
        ThrottleLimitExceededMomentarily,
        ThrottleLimitExceededSeconds,
        PerEndpointRequestThrottleExceeded,
        PerApplicationThrottleExceeded,
        PerApplicationAnonymousThrottleExceeded,
        PerApplicationAuthenticatedThrottleExceeded,
        PerUserThrottleExceeded,
        DestinyThrottledByGameServer,
    ];

    /// <summary>True when waiting and retrying is the right response.</summary>
    public static bool IsThrottle(int errorCode) => Throttles.Contains(errorCode);

    /// <summary>
    /// True when the thing asked for does not exist. Distinct from a privacy refusal, where
    /// the profile does exist and the dashboard should say so rather than render zeroes.
    /// </summary>
    public static bool IsNotFound(int errorCode) =>
        errorCode is DestinyAccountNotFound or DestinyCharacterNotFound or NotFound
            or UserCannotResolveCentralAccount or DestinyPGCRNotFound;

    /// <summary>What the operator should do, in plain language. Never null.</summary>
    public static string Remedy(int errorCode) => errorCode switch
    {
        ApiKeyMissingFromRequest =>
            "No X-API-Key header reached Bungie. Set the BUNGIE_API_KEY environment variable "
            + "(or Bungie:ApiKey in appsettings.Development.json) and restart. With no key at all "
            + "the tracker serves fixtures instead, which is the intended zero-credential path.",

        ApiInvalidOrExpiredKey =>
            "Bungie rejected the API key. Confirm it at https://www.bungie.net/en/Application. "
            + "Keys stop working when their application is disabled, and a space pasted in front "
            + "of the key is enough to fail this check.",

        ApiExceededMaxKeys =>
            "That Bungie application has too many keys. Remove an unused one at "
            + "https://www.bungie.net/en/Application.",

        DestinyPrivacyRestriction =>
            "The player's Destiny privacy settings hide this data. Only they can change it, under "
            + "Bungie.net account settings, Privacy. There is nothing to fix on this side.",

        DestinyAccountNotFound =>
            "No Destiny account for that membership type and id. Check the platform: a Steam "
            + "membership id is not valid against membershipType 1 (Xbox). Searching by Bungie name "
            + "with membershipType All avoids guessing.",

        DestinyCharacterNotFound =>
            "That character id does not belong to this profile, or has been deleted. Re-read the "
            + "character list from the Profiles component instead of caching character ids.",

        DestinyInvalidMembershipType =>
            "Destiny will not accept that membershipType here. Use a concrete platform "
            + "(1 Xbox, 2 PSN, 3 Steam, 6 Epic); All (-1) is only valid on the player search.",

        DestinyLegacyPlatformInaccessible =>
            "The account is on a platform Destiny 2 no longer serves. If the player has Cross Save "
            + "enabled, use the crossSaveOverride membership instead.",

        DestinyPGCRNotFound =>
            "No post game carnage report for that activity id. Reports age out, so treat a missing "
            + "one as normal for older matches rather than as an error.",

        SystemDisabled =>
            "Bungie has disabled this system, which they do routinely at weekly reset and during "
            + "maintenance. Retry later; there is no fix on this side.",

        DestinyShardRelayClientTimeout or DestinyDirectBabelClientTimeout or DestinyUnexpectedError =>
            "Bungie's own back end timed out. Retry, and if it persists check whether Destiny is in "
            + "maintenance.",

        InvalidParameters or ParameterParseFailure or ParameterNotFound =>
            "Bungie rejected a parameter. The usual causes are a Bungie name sent as one string "
            + "instead of displayName plus displayNameCode, or a daystart/dayend window wider than "
            + "the 31 days the stats endpoint allows.",

        _ when IsThrottle(errorCode) =>
            "Rate limited. Wait out ThrottleSeconds from the response, then retry. If this happens "
            + "constantly, lower ActivityPageSize or MaxActivityPages so fewer requests go out.",

        _ =>
            "Look this ErrorCode up in Exceptions.PlatformErrorCodes at https://bungie-net.github.io/. "
            + "The numeric code is the reliable identifier; ErrorStatus is only a display string.",
    };
}

/// <summary>Reading the envelope, and turning a failure into something actionable.</summary>
public static class BungieResponse
{
    /// <summary>
    /// Shared by the live client and the fixture handler on purpose. A fixture that
    /// deserializes through a different path is not exercising the code that runs in
    /// production.
    /// </summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Parse an envelope and hand back its payload, or throw with a remedy.</summary>
    /// <param name="context">
    /// What was being fetched, for the message. Must never contain the API key.
    /// </param>
    public static T Unwrap<T>(string json, string context)
    {
        var payload = UnwrapOptional<T>(json, context);
        if (payload is null)
        {
            throw Upstream(
                $"Bungie reported success but sent no payload for {context}.",
                "Some endpoints answer with an empty Response when there is genuinely nothing to "
                + "return, such as an activity history page past the end. Callers that expect that "
                + "should use UnwrapOptional and treat null as an empty result.");
        }

        return payload;
    }

    /// <summary>
    /// As <see cref="Unwrap{T}"/>, but a success with no payload yields <c>default</c>
    /// rather than throwing. Activity history past the last page does exactly that:
    /// ErrorCode 1, and no Response at all.
    /// </summary>
    public static T? UnwrapOptional<T>(string json, string context)
    {
        BungieEnvelope<T>? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<BungieEnvelope<T>>(json, Json);
        }
        catch (JsonException ex)
        {
            throw Upstream(
                $"Bungie returned something that is not a platform envelope for {context}.",
                "This usually means the request reached bungie.net rather than bungie.net/Platform, "
                + "or an interstitial page was served instead of the API. Check PlatformBaseUrl.",
                ex);
        }

        if (envelope is null)
        {
            throw Upstream(
                $"Bungie returned an empty body for {context}.",
                "Retry once. A persistently empty body points at a proxy between this process and "
                + "bungie.net rather than at Bungie.");
        }

        if (!envelope.IsSuccess)
        {
            throw ToException(envelope, context);
        }

        return envelope.Response;
    }

    /// <summary>
    /// A failure that is Bungie's, not the caller's: a body that is not an envelope at all,
    /// an empty response, a success with nothing in it.
    /// </summary>
    /// <remarks>
    /// These carry no ErrorCode, because there was no envelope to read one out of, and the
    /// HTTP boundary turns a codeless <see cref="TrackerException"/> into 400 Bad Request.
    /// Stamping 502 on them keeps "bungie.net served us an interstitial" from being
    /// reported to the dashboard as "you asked for the wrong thing".
    /// </remarks>
    private static TrackerException Upstream(string message, string remedy, Exception? inner = null)
    {
        var exception = new TrackerException(message, remedy, inner);
        exception.Data["httpStatus"] = 502;
        return exception;
    }

    /// <summary>
    /// Turn a failed envelope into a <see cref="TrackerException"/>. The numeric ErrorCode
    /// leads the message because that is the identifier worth searching for; ErrorStatus is
    /// a display string and Bungie has renamed them before.
    /// </summary>
    public static TrackerException ToException<T>(BungieEnvelope<T> envelope, string context)
    {
        var status = string.IsNullOrWhiteSpace(envelope.ErrorStatus) ? "Unknown" : envelope.ErrorStatus;
        var detail = string.IsNullOrWhiteSpace(envelope.Message) ? string.Empty : " " + envelope.Message;

        var message = string.Create(
            CultureInfo.InvariantCulture,
            $"Bungie refused {context}: ErrorCode {envelope.ErrorCode} ({status}).{detail}");

        var remedy = BungiePlatformError.Remedy(envelope.ErrorCode);
        if (BungiePlatformError.IsThrottle(envelope.ErrorCode) && envelope.ThrottleSeconds > 0)
        {
            remedy = string.Create(
                CultureInfo.InvariantCulture,
                $"{remedy} Bungie asked for {envelope.ThrottleSeconds}s.");
        }

        var exception = new TrackerException(message, remedy);
        exception.Data["errorCode"] = envelope.ErrorCode;
        exception.Data["errorStatus"] = status;
        if (envelope.ThrottleSeconds > 0)
        {
            exception.Data["throttleSeconds"] = envelope.ThrottleSeconds;
        }

        return exception;
    }
}
