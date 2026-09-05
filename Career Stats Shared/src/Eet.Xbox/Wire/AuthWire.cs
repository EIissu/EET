using System.Text.Json;
using System.Text.Json.Serialization;

namespace Eet.Xbox.Wire;

// ---------------------------------------------------------------------------------------
// Step 1: Azure AD device code flow.
// ---------------------------------------------------------------------------------------

/// <summary>What the device code endpoint hands back for the user to act on.</summary>
internal sealed record DeviceCodeResponse
{
    [JsonPropertyName("device_code")] public string? DeviceCode { get; init; }

    [JsonPropertyName("user_code")] public string? UserCode { get; init; }

    [JsonPropertyName("verification_uri")] public string? VerificationUri { get; init; }

    [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }

    /// <summary>Seconds to wait between polls. Poll faster and the endpoint says <c>slow_down</c>.</summary>
    [JsonPropertyName("interval")] public int Interval { get; init; }

    [JsonPropertyName("message")] public string? Message { get; init; }
}

/// <summary>A successful token response, or the error that says to keep waiting.</summary>
internal sealed record OAuthTokenResponse
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; init; }

    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }

    [JsonPropertyName("token_type")] public string? TokenType { get; init; }

    [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }

    [JsonPropertyName("scope")] public string? Scope { get; init; }

    [JsonPropertyName("error")] public string? Error { get; init; }

    [JsonPropertyName("error_description")] public string? ErrorDescription { get; init; }
}

// ---------------------------------------------------------------------------------------
// Step 2: user.auth.xboxlive.com. PascalCase on the wire, exactly as written here.
// ---------------------------------------------------------------------------------------

internal sealed record XblUserAuthRequest
{
    public required XblUserAuthProperties Properties { get; init; }

    public string RelyingParty { get; init; } = "http://auth.xboxlive.com";

    public string TokenType { get; init; } = "JWT";
}

internal sealed record XblUserAuthProperties
{
    public string AuthMethod { get; init; } = "RPS";

    public string SiteName { get; init; } = "user.auth.xboxlive.com";

    /// <summary>
    /// The Azure AD access token, prefixed <c>d=</c>. The prefix is not decoration: it
    /// tells the RPS endpoint the ticket is a delegated Azure AD token rather than a
    /// legacy MSA one, and omitting it fails with a bare 400.
    /// </summary>
    public required string RpsTicket { get; init; }
}

// ---------------------------------------------------------------------------------------
// Step 3: xsts.auth.xboxlive.com.
// ---------------------------------------------------------------------------------------

internal sealed record XstsAuthorizeRequest
{
    public required XstsAuthorizeProperties Properties { get; init; }

    public required string RelyingParty { get; init; }

    public string TokenType { get; init; } = "JWT";
}

internal sealed record XstsAuthorizeProperties
{
    public string SandboxId { get; init; } = "RETAIL";

    public required IReadOnlyList<string> UserTokens { get; init; }
}

/// <summary>The response shared by steps 2 and 3.</summary>
internal sealed record XblTokenResponse
{
    public string? IssueInstant { get; init; }

    /// <summary>When the token stops working, in the service's own words.</summary>
    public string? NotAfter { get; init; }

    public string? Token { get; init; }

    public XblDisplayClaims? DisplayClaims { get; init; }
}

internal sealed record XblDisplayClaims
{
    /// <summary>
    /// The Xbox user identity claims. Step 2 returns a <c>uhs</c> here too, but it is step
    /// 3's <c>uhs</c> that pairs with the XSTS token in the Authorization header.
    /// </summary>
    public IReadOnlyList<XblXuiClaim>? Xui { get; init; }
}

internal sealed record XblXuiClaim
{
    /// <summary>User hash. Half of <c>XBL3.0 x={uhs};{token}</c>.</summary>
    public string? Uhs { get; init; }

    /// <summary>The XUID, present on an XSTS token and absent from a user token.</summary>
    public string? Xid { get; init; }

    public string? Gtg { get; init; }
}

/// <summary>
/// The body Xbox returns with a 401 when it is the ACCOUNT it objects to rather than the
/// request. Shape confirmed against real failures: <c>{"Identity":"0","XErr":2148916233,
/// "Message":"","Redirect":"https://start.ui.xboxlive.com/CreateAccount"}</c>.
/// </summary>
internal sealed record XstsErrorResponse
{
    public string? Identity { get; init; }

    public long XErr { get; init; }

    public string? Message { get; init; }

    public string? Redirect { get; init; }
}

// ---------------------------------------------------------------------------------------
// Step 4: the Spartan token.
// ---------------------------------------------------------------------------------------

internal sealed record SpartanTokenRequest
{
    public string Audience { get; init; } = "urn:343:s3:services";

    public string MinVersion { get; init; } = "4";

    public required IReadOnlyList<SpartanTokenProof> Proof { get; init; }
}

internal sealed record SpartanTokenProof
{
    public required string Token { get; init; }

    public string TokenType { get; init; } = "Xbox_XSTSv3";
}

/// <summary>
/// The Spartan token response.
///
/// <see cref="ExpiresUtc"/> is the field worth flagging: it is NOT a bare timestamp
/// string, it is an object wrapping one -- <c>{"ExpiresUtc":{"ISO8601Date":"..."}}</c>.
/// Reading it as a string yields null, the token is treated as already expired, and the
/// client renews on every single call. <see cref="Iso8601Date"/> models the wrapper, and
/// <see cref="Iso8601DateConverter"/> also accepts the bare-string form in case the
/// service ever flattens it.
/// </summary>
internal sealed record SpartanTokenResponse
{
    public string? SpartanToken { get; init; }

    public Iso8601Date? ExpiresUtc { get; init; }

    /// <summary>A .NET-formatted duration such as <c>03:59:59.6759884</c>.</summary>
    public string? TokenDuration { get; init; }
}

/// <summary>
/// The <c>{"ISO8601Date": "..."}</c> wrapper 343's services put around every timestamp.
/// The converter also accepts a bare string, so a future flattening of the shape degrades
/// to "still works" rather than "silently expires every token".
/// </summary>
[JsonConverter(typeof(Iso8601DateConverter))]
internal sealed record Iso8601Date(string? Value);

internal sealed class Iso8601DateConverter : JsonConverter<Iso8601Date>
{
    public override Iso8601Date? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.String:
                return new Iso8601Date(reader.GetString());

            case JsonTokenType.StartObject:
            {
                string? value = null;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName)
                    {
                        continue;
                    }

                    var name = reader.GetString();
                    reader.Read();
                    if (string.Equals(name, "ISO8601Date", StringComparison.OrdinalIgnoreCase)
                        && reader.TokenType == JsonTokenType.String)
                    {
                        value = reader.GetString();
                    }
                    else
                    {
                        reader.Skip();
                    }
                }

                return new Iso8601Date(value);
            }

            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, Iso8601Date value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStartObject();
        writer.WriteString("ISO8601Date", value.Value);
        writer.WriteEndObject();
    }
}
