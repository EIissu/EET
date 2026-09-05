using System.Text.Json;
using Eet.Halo.Api;
using Eet.Trackers.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Eet.Halo.Tests;

/// <summary>
/// What the HTTP layer promises: RFC 7807 with a usable remedy, JSON a browser can read
/// without a parser of its own, and an app that starts whether or not the dashboard exists.
/// </summary>
public sealed class ApiContractTests
{
    [Fact]
    public void ATrackerExceptionBecomesProblemDetailsWithTheRemedyInDetail()
    {
        var error = new TrackerException(
            "Halo request failed with HTTP 401.",
            "The Spartan token expired. Sign in again; it is short-lived by design.");

        var problem = ApiProblems.From(error, "/api/career");

        Assert.Equal(StatusCodes.Status502BadGateway, problem.Status);
        Assert.Equal(error.Message, problem.Title);

        // The contract: detail always says what to do about it.
        Assert.Equal(error.Remedy, problem.Detail);
        Assert.Equal(error.Remedy, problem.Extensions[ApiProblems.RemedyExtension]);
        Assert.Equal("/api/career", problem.Instance);
    }

    [Fact]
    public void ATrackerExceptionWithNoRemedyStillGetsUsableDetail()
    {
        var problem = ApiProblems.From(new TrackerException("Something went wrong."), "/x");

        Assert.False(string.IsNullOrWhiteSpace(problem.Detail));
    }

    [Fact]
    public void AnUnexpectedExceptionIsA500AndDoesNotLeakItsMessage()
    {
        // The message of an arbitrary exception can contain a path, a connection string, or
        // a token. It belongs in the log, not in the response.
        var problem = ApiProblems.From(new InvalidOperationException("connection string: secret"), "/x");

        Assert.Equal(StatusCodes.Status500InternalServerError, problem.Status);
        Assert.DoesNotContain("secret", problem.Title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", problem.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DurationsSerialiseAsSecondsRatherThanAsAClockFace()
    {
        var json = JsonSerializer.Serialize(new { d = TimeSpan.FromMinutes(12.5) }, ApiJson.Options);

        Assert.Equal("""{"d":750}""", json);
    }

    [Fact]
    public void EnumsSerialiseAsNamesSoAChartKnowsWhichWayIsGood()
    {
        var json = JsonSerializer.Serialize(new { better = Better.Higher, game = GameId.HaloInfinite }, ApiJson.Options);

        Assert.Contains("\"Higher\"", json, StringComparison.Ordinal);
        Assert.Contains("\"HaloInfinite\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWholeSnapshotSerialisesCleanly()
    {
        var source = TestEnv.FixtureSource();
        var snapshot = await source.GetSnapshotAsync(new Player("t", TestEnv.Xuid, "Xbox"));

        // System.Text.Json refuses NaN and Infinity, so this also proves no metric divided
        // by zero on the way here.
        var json = JsonSerializer.Serialize(snapshot, ApiJson.Options);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.True(root.GetProperty("isFixture").GetBoolean());
        Assert.Equal("HaloInfinite", root.GetProperty("game").GetString());
        Assert.True(root.GetProperty("headline").GetArrayLength() > 0);
        Assert.True(root.GetProperty("trends").GetArrayLength() > 0);

        // Durations came through as numbers, and dates as plain ISO days.
        Assert.Equal(JsonValueKind.Number, root.GetProperty("totals").GetProperty("timePlayed").ValueKind);
        var firstPoint = root.GetProperty("trends")[0].GetProperty("points")[0];
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", firstPoint.GetProperty("date").GetString()!);

        // The computed convenience properties the dashboard wants are present.
        Assert.True(root.GetProperty("totals").TryGetProperty("winRate", out _));
        Assert.True(root.GetProperty("recent")[0].TryGetProperty("kd", out _));
    }

    [Fact]
    public void AMissingDashboardDirectoryIsNotAStartupFailure()
    {
        // Another agent is building Career Stats Shared/web. Until it exists, the API still has
        // to run.
        Assert.Null(StaticAssets.Locate("no-such-directory-anywhere", AppContext.BaseDirectory));
        Assert.NotNull(StaticAssets.Locate("Career Stats Shared/fixtures", AppContext.BaseDirectory));
    }

    [Fact]
    public void CredentialDetectionReportsNamesAndNeverValues()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Xbox:ClientId"] = "an-id-that-must-not-be-echoed",
                ["Xbox:ClientSecret"] = "a-secret-that-must-not-be-echoed",
            })
            .Build();

        var json = JsonSerializer.Serialize(CredentialHints.Detect(configuration), ApiJson.Options);

        Assert.Contains("Xbox:ClientId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-be-echoed", json, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoConfigurationTheAnswerSaysFixturesAreTheSupportedMode()
    {
        var json = JsonSerializer.Serialize(
            CredentialHints.Detect(new ConfigurationBuilder().Build()),
            ApiJson.Options);

        Assert.Contains("not a failure", json, StringComparison.OrdinalIgnoreCase);
    }
}
