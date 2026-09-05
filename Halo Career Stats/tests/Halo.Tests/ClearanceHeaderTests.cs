using System.Globalization;
using System.Net;
using Eet.Halo.Client;
using Eet.Halo.Client.Endpoints;
using Eet.Halo.Client.Http;
using Eet.Trackers.Core;
using Xunit;

namespace Eet.Halo.Tests;

/// <summary>
/// The clearance-vs-not distinction, which is the single easiest thing to get wrong about
/// these services and the reason the endpoint manifest is embedded rather than transcribed.
/// </summary>
public sealed class ClearanceHeaderTests
{
    private static readonly HaloEndpointResolver Endpoints = TestEnv.Endpoints;

    [Theory]
    [InlineData(HaloEndpointIds.MatchHistory, false)]
    [InlineData(HaloEndpointIds.MatchCount, false)]
    [InlineData(HaloEndpointIds.MatchStats, false)]
    [InlineData(HaloEndpointIds.MatchSkill, true)]
    [InlineData(HaloEndpointIds.PlaylistCsr, true)]
    [InlineData(HaloEndpointIds.UgcMap, true)]
    [InlineData(HaloEndpointIds.UgcGameVariant, true)]
    [InlineData(HaloEndpointIds.Clearance, false)]
    public void ManifestSaysWhichEndpointsAreClearanceAware(string endpointId, bool expected)
    {
        // Reading it out of 343's own manifest, not out of a constant we typed.
        Assert.Equal(expected, Endpoints.Resolve(endpointId).ClearanceAware);
    }

    [Fact]
    public void ValidateAcceptsTheManifestAsShipped() => Endpoints.Validate();

    [Theory]
    [InlineData(HaloEndpointIds.MatchHistory)]
    [InlineData(HaloEndpointIds.MatchCount)]
    [InlineData(HaloEndpointIds.MatchStats)]
    public async Task StatsEndpointsSendSpartanOnlyAndNeverClearance(string endpointId)
    {
        var (stub, client) = Build(clearance: "flight-123");
        await Send(client, endpointId);

        var headers = Assert.Single(stub.Headers);
        Assert.Equal("test-spartan-token", Assert.Single(headers[HaloAuthHandler.SpartanHeader]));

        // The point of the test. Sending clearance here is not merely unnecessary: it makes
        // every stats request depend on a flight lookup that goes stale with each game build.
        Assert.False(headers.ContainsKey(HaloAuthHandler.ClearanceHeader));
    }

    [Theory]
    [InlineData(HaloEndpointIds.MatchSkill)]
    [InlineData(HaloEndpointIds.PlaylistCsr)]
    [InlineData(HaloEndpointIds.UgcMap)]
    public async Task ClearanceAwareEndpointsSendBothHeaders(string endpointId)
    {
        var (stub, client) = Build(clearance: "flight-123");
        await Send(client, endpointId);

        var headers = Assert.Single(stub.Headers);
        Assert.Equal("test-spartan-token", Assert.Single(headers[HaloAuthHandler.SpartanHeader]));
        Assert.Equal("flight-123", Assert.Single(headers[HaloAuthHandler.ClearanceHeader]));
    }

    [Fact]
    public async Task AClearanceAwareEndpointFailsLocallyWhenClearanceIsMissing()
    {
        var (stub, client) = Build(clearance: null);

        var error = await Assert.ThrowsAsync<TrackerException>(
            () => Send(client, HaloEndpointIds.MatchSkill));

        // Fail before the request leaves, not after the server rejects it, and say what it
        // costs: rank, not match history.
        Assert.Empty(stub.Requests);
        Assert.Contains("clearance-aware", error.Message, StringComparison.Ordinal);
        Assert.Contains("match history", error.Remedy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AStatsEndpointStillWorksWithNoClearanceAtAll()
    {
        // The consequence that matters: losing the flight id costs rank and asset names and
        // nothing else.
        var (stub, client) = Build(clearance: null);
        await Send(client, HaloEndpointIds.MatchHistory);

        var headers = Assert.Single(stub.Headers);
        Assert.True(headers.ContainsKey(HaloAuthHandler.SpartanHeader));
        Assert.False(headers.ContainsKey(HaloAuthHandler.ClearanceHeader));
    }

    [Fact]
    public void EndpointsResolveToTheAuthorityTheManifestNames()
    {
        Assert.Equal("halostats.svc.halowaypoint.com", Endpoints.Resolve(HaloEndpointIds.MatchHistory).Authority.Hostname);
        Assert.Equal("skill.svc.halowaypoint.com", Endpoints.Resolve(HaloEndpointIds.MatchSkill).Authority.Hostname);
        Assert.Equal("settings.svc.halowaypoint.com", Endpoints.Resolve(HaloEndpointIds.Clearance).Authority.Hostname);
        Assert.Equal(
            "discovery-infiniteugc.svc.halowaypoint.com",
            Endpoints.Resolve(HaloEndpointIds.UgcMap).Authority.Hostname);
    }

    [Fact]
    public void TheServiceRecordIsMarkedAsNotComingFromTheManifest()
    {
        // Its provenance is weaker than everything else and the code has to know that.
        Assert.False(Endpoints.IsPublished(HaloEndpointIds.ServiceRecord));
        Assert.True(Endpoints.IsPublished(HaloEndpointIds.MatchHistory));

        var endpoint = Endpoints.Resolve(HaloEndpointIds.ServiceRecord);
        Assert.Equal("halostats.svc.halowaypoint.com", endpoint.Authority.Hostname);
        Assert.False(endpoint.ClearanceAware);
    }

    [Fact]
    public void XuidParenthesesSurviveUrlBuilding()
    {
        // xuid(2814...) percent-encoded to xuid%282814...%29 is a silent, empty-result-set
        // kind of wrong.
        var call = HaloCall.Create(
            Endpoints.Resolve(HaloEndpointIds.MatchHistory),
            HaloCachePolicy.Short,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["player"] = Identity.XuidRef(TestEnv.Xuid) },
            [new KeyValuePair<string, string>("count", "25")]);

        Assert.Equal(
            string.Create(CultureInfo.InvariantCulture, $"/hi/players/xuid({TestEnv.Xuid})/matches?count=25"),
            call.PathAndQuery);
        Assert.DoesNotContain("%28", call.Uri.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingPathArgumentIsAnActionableError()
    {
        var call = HaloCall.Create(Endpoints.Resolve(HaloEndpointIds.MatchStats));
        var error = Assert.Throws<TrackerException>(() => call.PathAndQuery);
        Assert.Contains("matchId", error.Message, StringComparison.Ordinal);
        Assert.NotNull(error.Remedy);
    }

    private static (StubHandler Stub, HttpHaloTransport Client) Build(string? clearance)
    {
        var stub = new StubHandler();
        var http = HaloTrackerSetup.CreateHttpClient(
            TestEnv.Options(),
            new FakeXboxAuth(),
            new StaticClearanceProvider(clearance),
            loggerFactory: null,
            primary: stub);

        return (stub, new HttpHaloTransport(http));
    }

    private static Task<string> Send(HttpHaloTransport transport, string endpointId)
    {
        var endpoint = TestEnv.Endpoints.Resolve(endpointId);
        var args = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["player"] = Identity.XuidRef(TestEnv.Xuid),
            ["matchId"] = "11111111-2222-3333-4444-555555555555",
            ["playlistId"] = "edfef3ac-9cbe-4fa2-b949-8f29deafd483",
            ["assetId"] = "66666666-7777-8888-9999-aaaaaaaaaaaa",
            ["versionId"] = "bbbbbbbb-cccc-dddd-eeee-ffffffffffff",
            ["audience"] = "RETAIL",
            ["sandbox"] = "UNUSED",
            ["buildNumber"] = "000000.00.00.00.0000-0",
        };

        return transport.GetJsonAsync(HaloCall.Create(endpoint, HaloCachePolicy.None, args));
    }
}
