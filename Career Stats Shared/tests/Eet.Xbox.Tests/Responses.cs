using System.Globalization;

namespace Eet.Xbox.Tests;

/// <summary>
/// Canned Xbox responses, in the exact shape the live services return.
///
/// These are deliberately not the fixtures in Career Stats Shared/fixtures -- those are a
/// 90-day synthetic career for the dashboard to render, and are far too large to reason
/// about in a unit test. These are minimal, hand-written, and each one exists to pin one
/// specific detail of the wire format.
/// </summary>
internal static class Responses
{
    internal const string UserToken = "eyJTYW1wbGUiOiJ1c2VyLXRva2VuIn0";

    internal const string XstsTokenValue = "eyJTYW1wbGUiOiJ4c3RzLXRva2VuIn0";

    internal const string UserHash = "1234567890123456789";

    internal const string Xuid = "2814648798129555";

    /// <summary>Step 2's answer. Note DisplayClaims carries a uhs but no xid.</summary>
    internal static string UserAuthenticate(string? notAfter = null) => $$"""
        {
          "IssueInstant": "2026-09-04T12:00:00.0000000Z",
          "NotAfter": "{{notAfter ?? "2026-09-05T12:00:00.0000000Z"}}",
          "Token": "{{UserToken}}",
          "DisplayClaims": { "xui": [ { "uhs": "{{UserHash}}" } ] }
        }
        """;

    /// <summary>Step 3's answer, with both halves of the Authorization header.</summary>
    internal static string XstsAuthorize(DateTimeOffset notAfter) => $$"""
        {
          "IssueInstant": "2026-09-04T12:00:00.0000000Z",
          "NotAfter": "{{notAfter.ToString("o", CultureInfo.InvariantCulture)}}",
          "Token": "{{XstsTokenValue}}",
          "DisplayClaims": { "xui": [ { "uhs": "{{UserHash}}", "xid": "{{Xuid}}", "gtg": "Fixture Player" } ] }
        }
        """;

    /// <summary>
    /// The 401 body that carries the real reason. Shape confirmed against live failures:
    /// Identity is the string "0", XErr is a number, Redirect points at an Xbox page.
    /// </summary>
    internal static string XErr(long code, string redirect = "https://start.ui.xboxlive.com/CreateAccount") => $$"""
        { "Identity": "0", "XErr": {{code}}, "Message": "", "Redirect": "{{redirect}}" }
        """;

    /// <summary>
    /// The Spartan token response, with ExpiresUtc as the ISO8601Date-wrapping OBJECT the
    /// service really sends rather than the bare string it is often assumed to be.
    /// </summary>
    internal static string SpartanToken(
        string token = "v4=fixture.spartan.token",
        string expires = "2026-09-04T16:00:00.000Z",
        string duration = "03:59:59.6759884") => $$"""
        {
          "SpartanToken": "{{token}}",
          "ExpiresUtc": { "ISO8601Date": "{{expires}}" },
          "TokenDuration": "{{duration}}"
        }
        """;

    /// <summary>The same thing with ExpiresUtc flattened to a string, which must still parse.</summary>
    internal static string SpartanTokenFlatExpiry(string expires = "2026-09-04T16:00:00.000Z") => $$"""
        { "SpartanToken": "v4=fixture.spartan.token", "ExpiresUtc": "{{expires}}" }
        """;

    internal const string DeviceCodeStart = """
        {
          "device_code": "DEVICE-CODE-FIXTURE",
          "user_code": "ABCD-EFGH",
          "verification_uri": "https://microsoft.com/link",
          "expires_in": 900,
          "interval": 5,
          "message": "Open https://microsoft.com/link and enter ABCD-EFGH."
        }
        """;

    internal const string AuthorizationPending = """
        { "error": "authorization_pending", "error_description": "The user has not completed sign-in." }
        """;

    internal const string SlowDown = """
        { "error": "slow_down", "error_description": "Polling too frequently." }
        """;

    internal const string InvalidGrant = """
        { "error": "invalid_grant", "error_description": "The refresh token has expired." }
        """;

    internal static string OAuthSuccess(string accessToken = "azure-ad-access-token", int expiresIn = 3600) => $$"""
        {
          "token_type": "Bearer",
          "scope": "XboxLive.signin XboxLive.offline_access",
          "expires_in": {{expiresIn}},
          "access_token": "{{accessToken}}",
          "refresh_token": "azure-ad-refresh-token"
        }
        """;

    /// <summary>
    /// Two achievements exercising the awkward bits at once: gamerscore as a string inside
    /// rewards, progressState as text, the year-1 sentinel on the locked one, a missing
    /// rarity block, and a locked description that differs from the unlocked one.
    /// </summary>
    internal const string Achievements = """
        {
          "achievements": [
            {
              "id": "1",
              "serviceConfigId": "3f8f4d1a-9c25-4b0d-9a67-1f1b2f2a51e7",
              "name": "Zone Control: Bronze",
              "titleAssociations": [ { "name": "Halo Infinite", "id": 2043073184 } ],
              "progressState": "Achieved",
              "progression": {
                "requirements": [
                  { "id": "r1", "current": "25", "target": "25", "valueType": "Integer" }
                ],
                "timeUnlocked": "2026-07-14T20:31:07.4720000Z"
              },
              "mediaAssets": [
                { "name": "icon", "type": "Icon", "url": "https://images-eds-ssl.xboxlive.com/image?url=one" }
              ],
              "isSecret": false,
              "description": "You captured twenty-five zones.",
              "lockedDescription": "Capture 25 zones in matchmaking",
              "rewards": [
                { "name": null, "description": null, "value": "20", "type": "Gamerscore", "valueType": "Int" },
                { "name": "Visor", "description": null, "value": "1", "type": "InApp", "valueType": "String" }
              ],
              "isRevoked": false,
              "rarity": { "currentCategory": "Rare", "currentPercentage": 4.5 }
            },
            {
              "id": "2",
              "serviceConfigId": "3f8f4d1a-9c25-4b0d-9a67-1f1b2f2a51e7",
              "name": "Flag Runner: Gold",
              "titleAssociations": [ { "name": "Halo Infinite", "id": 2043073184 } ],
              "progressState": "InProgress",
              "progression": {
                "requirements": [
                  { "id": "r2", "current": "30", "target": "100", "valueType": "Integer" },
                  { "id": "r3", "current": "10", "target": "50", "valueType": "Integer" }
                ],
                "timeUnlocked": "0001-01-01T00:00:00.0000000"
              },
              "mediaAssets": [
                { "name": "icon", "type": "Icon", "url": "https://images-eds-ssl.xboxlive.com/image?url=two" }
              ],
              "isSecret": false,
              "description": "You scored one hundred flag captures.",
              "lockedDescription": "Score 100 flag captures",
              "rewards": [
                { "name": null, "description": null, "value": "50", "type": "Gamerscore", "valueType": "Int" }
              ],
              "isRevoked": false
            }
          ],
          "pagingInfo": { "continuationToken": null, "totalRecords": 2 }
        }
        """;

    /// <summary>Page one of a two-page response, so the pagination loop has somewhere to go.</summary>
    internal const string AchievementsPageOne = """
        {
          "achievements": [
            {
              "id": "1",
              "name": "First",
              "titleAssociations": [ { "name": "Halo Infinite", "id": 2043073184 } ],
              "progressState": "Achieved",
              "progression": { "requirements": [], "timeUnlocked": "2026-07-01T10:00:00.0000000Z" },
              "rewards": [ { "value": "10", "type": "Gamerscore", "valueType": "Int" } ]
            }
          ],
          "pagingInfo": { "continuationToken": "PAGE-TWO", "totalRecords": 2 }
        }
        """;

    internal const string AchievementsPageTwo = """
        {
          "achievements": [
            {
              "id": "2",
              "name": "Second",
              "titleAssociations": [ { "name": "Halo Infinite", "id": 2043073184 } ],
              "progressState": "NotStarted",
              "progression": { "requirements": [], "timeUnlocked": "0001-01-01T00:00:00.0000000" },
              "rewards": [ { "value": "15", "type": "Gamerscore", "valueType": "Int" } ]
            }
          ],
          "pagingInfo": { "continuationToken": null, "totalRecords": 2 }
        }
        """;

    internal const string TitleHistory = """
        {
          "xuid": "2814648798129555",
          "titles": [
            {
              "titleId": "2043073184",
              "name": "Halo Infinite",
              "type": "Game",
              "devices": [ "PC", "XboxSeries" ],
              "displayImage": "https://store-images.s-microsoft.com/image/boxart",
              "achievement": {
                "currentAchievements": 1,
                "totalAchievements": 119,
                "currentGamerscore": 20,
                "totalGamerscore": 2420,
                "progressPercentage": 0.8,
                "sourceVersion": 1
              },
              "titleHistory": { "lastTimePlayed": "2026-09-01T18:45:00.0000000Z", "visible": true }
            }
          ]
        }
        """;

    internal const string Profile = """
        {
          "profileUsers": [
            {
              "id": "2814648798129555",
              "hostId": "2814648798129555",
              "settings": [
                { "id": "Gamertag", "value": "Fixture Player" },
                { "id": "GameDisplayPicRaw", "value": "https://images-eds-ssl.xboxlive.com/image?url=pic" },
                { "id": "Gamerscore", "value": "1475" }
              ],
              "isSponsoredUser": false
            }
          ]
        }
        """;
}
