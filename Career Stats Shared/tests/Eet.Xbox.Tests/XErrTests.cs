using System.Net;
using Eet.Trackers.Core;

namespace Eet.Xbox.Tests;

/// <summary>
/// The XErr mapping.
///
/// Every one of these is a 401 on the wire. Without this mapping they are one
/// indistinguishable "401 Unauthorized" -- and four of the five are things the user can
/// fix in about a minute if only somebody tells them which one it is.
/// </summary>
public sealed class XErrTests
{
    [Theory]
    [InlineData(2148916233, "xbox.com")]
    [InlineData(2148916238, "family")]
    [InlineData(2148916235, "region")]
    [InlineData(2148916227, "enforcement")]
    public void Each_known_code_gets_a_specific_remedy(long code, string expectedInRemedy)
    {
        var error = XstsErrors.Translate(HttpStatusCode.Unauthorized, Responses.XErr(code), "XSTS authorization");

        Assert.Contains(code.ToString(System.Globalization.CultureInfo.InvariantCulture), error.Message, StringComparison.Ordinal);
        Assert.NotNull(error.Remedy);
        Assert.Contains(expectedInRemedy, error.Remedy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_xbox_account_names_the_actual_problem()
    {
        var error = XstsErrors.Translate(
            HttpStatusCode.Unauthorized,
            Responses.XErr(XstsErrors.NoXboxAccount),
            "XSTS authorization");

        Assert.Contains("no Xbox profile", error.Message, StringComparison.OrdinalIgnoreCase);

        // The remedy has to be actionable. "Sign in at xbox.com once" is a two-minute fix
        // that a bare 401 would never have led anyone to.
        Assert.Contains("Sign in once", error.Remedy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_child_account_says_an_adult_is_needed()
    {
        var error = XstsErrors.Translate(
            HttpStatusCode.Unauthorized,
            Responses.XErr(XstsErrors.ChildAccount, "https://start.ui.xboxlive.com/AddChildToFamily"),
            "XSTS authorization");

        Assert.Contains("child account", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("adult", error.Remedy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_ban_says_plainly_that_nothing_here_will_fix_it()
    {
        var error = XstsErrors.Translate(
            HttpStatusCode.Unauthorized,
            Responses.XErr(XstsErrors.AccountBanned),
            "XSTS authorization");

        Assert.Contains("banned", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no change to this tool", error.Remedy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unknown_code_is_reported_rather_than_swallowed()
    {
        var error = XstsErrors.Translate(HttpStatusCode.Unauthorized, Responses.XErr(2148916999), "XSTS authorization");

        Assert.Contains("2148916999", error.Message, StringComparison.Ordinal);
        Assert.Contains("2148916999", error.Remedy, StringComparison.Ordinal);
    }

    [Fact]
    public void A_401_with_no_xerr_is_a_different_message_from_one_with_an_xerr()
    {
        var error = XstsErrors.Translate(HttpStatusCode.Unauthorized, "{}", "XSTS authorization");

        Assert.DoesNotContain("XErr", error.Message, StringComparison.Ordinal);
        Assert.Contains("401", error.Message, StringComparison.Ordinal);
        Assert.Contains("expired between steps", error.Remedy, StringComparison.Ordinal);
    }

    [Fact]
    public void A_400_points_at_the_d_prefix_because_that_is_what_causes_it()
    {
        var error = XstsErrors.Translate(HttpStatusCode.BadRequest, string.Empty, "Xbox user authentication");

        Assert.Contains("d=", error.Remedy, StringComparison.Ordinal);
    }

    [Fact]
    public void An_html_error_page_does_not_break_the_parser()
    {
        var error = XstsErrors.Translate(
            HttpStatusCode.BadGateway,
            "<html><head><title>502 Bad Gateway</title></head></html>",
            "XSTS authorization");

        Assert.Contains("502", error.Message, StringComparison.Ordinal);
        Assert.NotNull(error.Remedy);
    }

    [Fact]
    public void An_empty_body_does_not_break_the_parser()
    {
        Assert.Null(XstsErrors.ExtractXErr(null));
        Assert.Null(XstsErrors.ExtractXErr(string.Empty));
        Assert.Null(XstsErrors.ExtractXErr("   "));
        Assert.Null(XstsErrors.ExtractXErr("not json at all"));
    }

    [Fact]
    public void The_stage_name_is_carried_so_the_user_knows_which_step_failed()
    {
        var error = XstsErrors.Translate(
            HttpStatusCode.Unauthorized,
            Responses.XErr(XstsErrors.NoXboxAccount),
            "XSTS authorization");

        Assert.StartsWith("XSTS authorization:", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_live_chain_surfaces_an_xerr_rather_than_a_raw_401()
    {
        var stub = new StubHandler()
            .Route("/oauth2/v2.0/token", Responses.OAuthSuccess())
            .Route("user.auth.xboxlive.com", Responses.UserAuthenticate())
            .Route("xsts.auth.xboxlive.com", HttpStatusCode.Unauthorized, Responses.XErr(XstsErrors.NoXboxAccount));

        using var http = stub.Client();
        using var auth = new XboxAuth(
            http,
            new XboxOptions { ClientId = "00000000-0000-0000-0000-00000000c0de" },
            new MemoryTokenStore
            {
                Current = new CachedRefreshToken { RefreshToken = "r", ClientId = "00000000-0000-0000-0000-00000000c0de" },
            },
            new RecordingPrompt(),
            new TestClock());

        var error = await Assert.ThrowsAsync<TrackerException>(
            () => auth.GetXstsTokenAsync(RelyingParty.XboxLive));

        Assert.Contains("2148916233", error.Message, StringComparison.Ordinal);
        Assert.Contains("xbox.com", error.Remedy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_private_profile_is_named_as_a_privacy_setting_not_an_error()
    {
        var stub = new StubHandler()
            .Route("achievements.xboxlive.com", HttpStatusCode.Forbidden, "{}");

        using var http = stub.Client();
        var client = new XboxAchievementsClient(http, new FixtureXboxAuth(new TestClock()));

        var error = await Assert.ThrowsAsync<TrackerException>(
            () => client.GetTitleAchievementsAsync(FixtureXboxAuth.FixtureXuid, XboxTitles.HaloInfinite));

        Assert.Contains("privacy settings", error.Remedy, StringComparison.OrdinalIgnoreCase);
    }
}
