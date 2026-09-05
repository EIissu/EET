using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Eet.Destiny.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Eet.Destiny.Tests;

/// <summary>
/// The whole app, over a real HTTP pipeline, in-process and without a socket or a Bungie key.
///
/// Routing policy is the one thing that cannot be unit tested: whether an unknown /api path
/// comes back as JSON or as the single-page app is decided by middleware order and endpoint
/// precedence, not by any function. The only honest way to assert it is to make the request.
/// </summary>
public sealed class SiteEndpointTests : IClassFixture<DestinySite>
{
    /// <summary>The synthetic Guardian the fixtures describe.</summary>
    private const string MembershipId = "4611686018400119004";

    private const string BungieName = "AnaGuardian#4412";

    private readonly DestinySite _site;

    public SiteEndpointTests(DestinySite site) => _site = site;

    // -----------------------------------------------------------------------------------
    // The fallback, and the line it must not cross
    // -----------------------------------------------------------------------------------

    [Fact]
    public async Task AnUnknownApiRouteIsAJsonProblemAndNeverThePage()
    {
        using var client = _site.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/nope", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        // The failure this guards against is silent: fetch() succeeds, JSON.parse dies on
        // "Unexpected token <", and nothing says the path was wrong.
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<!doctype", body, StringComparison.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(body);
        Assert.Equal(404, document.RootElement.GetProperty("status").GetInt32());
        Assert.Contains(
            "/api/player",
            document.RootElement.GetProperty("detail").GetString()!,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownApiRouteUnderAnExistingOneIsStillJson()
    {
        using var client = _site.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/career/extra/segments", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task AnUnknownPageIsTheAppSoADeepLinkSurvivesAReload()
    {
        // The site keeps its state in the query string today, and /?game=destiny&q=... works
        // because "/" is a real file. This is the promise for the day it uses paths instead.
        Assert.NotNull(WebAssets.Choose());

        using var client = _site.CreateClient();

        using var response = await client.GetAsync(new Uri("/destiny/AnaGuardian/career", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

        // index.html names the content-hashed bundles, so caching it breaks the next deploy.
        Assert.True(response.Headers.CacheControl?.NoCache);
    }

    [Fact]
    public async Task APageWhosePathMerelyStartsWithApiIsStillThePage()
    {
        // "/apiary" is not "/api". A prefix check rather than a segment check would answer
        // this with a JSON 404 and break a perfectly ordinary route.
        Assert.NotNull(WebAssets.Choose());

        using var client = _site.CreateClient();

        using var response = await client.GetAsync(new Uri("/apiary", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task TheRootServesAFrontEndAndHealthSaysWhichOne()
    {
        using var client = _site.CreateClient();

        using var page = await client.GetAsync(new Uri("/", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);

        var health = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/health", UriKind.Relative));
        var web = health.GetProperty("web");
        Assert.True(web.GetProperty("present").GetBoolean());
        Assert.False(string.IsNullOrEmpty(web.GetProperty("kind").GetString()));
    }

    // -----------------------------------------------------------------------------------
    // Search, as a person actually types it
    // -----------------------------------------------------------------------------------

    [Theory]
    [InlineData(BungieName)]
    [InlineData("%20%20" + BungieName + "%20%20")]
    [InlineData(MembershipId)]
    [InlineData("%20" + MembershipId + "%20")]
    // A Halo reference pasted into the Destiny box, which is what a game switcher invites.
    [InlineData("xuid(" + MembershipId + ")")]
    public async Task WhatSomebodyPastedStillResolves(string raw)
    {
        using var client = _site.CreateClient();

        var found = await client.GetFromJsonAsync<JsonElement>(
            new Uri("/api/player?q=" + raw.Replace("#", "%23", StringComparison.Ordinal), UriKind.Relative));

        Assert.Equal(MembershipId, found.GetProperty("id").GetString());
        Assert.Equal(BungieName, found.GetProperty("handle").GetString());
    }

    [Fact]
    public async Task AMissAnswersWithSomethingAPersonCanActOn()
    {
        using var client = _site.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/api/player?q=NobodyHere%230001", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var detail = document.RootElement.GetProperty("detail").GetString()!;

        // Not just "no such player". Why a correct-looking name can never match.
        Assert.Contains("membership id", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AMissOnANameThatIsNotTheTextItLooksLikeNamesTheCodePoint()
    {
        using var client = _site.CreateClient();

        // "%D0%86lissu" is U+0406 followed by "lissu": it renders as "Ilissu" and is not.
        // Identity.Explain is the only thing that can tell somebody why their eyes and the
        // API disagree.
        using var response = await client.GetAsync(
            new Uri("/api/player?q=%D0%86lissu%230001", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains(
            "U+0406",
            document.RootElement.GetProperty("detail").GetString()!,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyQueryIsABadRequestRatherThanAnEmptySearch()
    {
        using var client = _site.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/player?q=%20%20", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // -----------------------------------------------------------------------------------
    // The shape the site is typed against
    // -----------------------------------------------------------------------------------

    [Fact]
    public async Task ACareerSaysItIsSyntheticAndCountsItsDurationsInSeconds()
    {
        using var client = _site.CreateClient();

        var career = await client.GetFromJsonAsync<JsonElement>(
            new Uri("/api/career?q=" + MembershipId, UriKind.Relative));

        // Invented data must never be mistaken for somebody's real career.
        Assert.True(career.GetProperty("isFixture").GetBoolean());

        // A TimeSpan written as "24.22:30:00" is a string, and the site's types say number.
        // Two APIs behind one game switcher cannot disagree about this.
        var timePlayed = career.GetProperty("totals").GetProperty("timePlayed");
        Assert.Equal(JsonValueKind.Number, timePlayed.ValueKind);
        Assert.True(timePlayed.GetDouble() > 0);

        var duration = career.GetProperty("recent")[0].GetProperty("duration");
        Assert.Equal(JsonValueKind.Number, duration.ValueKind);
        Assert.True(duration.GetDouble() > 0);

        // Rates stay fractions in [0,1]. Nothing in this pipeline turns them into percents.
        var winRate = career.GetProperty("totals").GetProperty("winRate").GetDouble();
        Assert.InRange(winRate, 0d, 1d);
    }

    // -----------------------------------------------------------------------------------
    // CORS
    // -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("http://localhost:5173")]
    [InlineData("http://127.0.0.1:5173")]
    public async Task TheViteDevServerMayCallInDevelopment(string origin)
    {
        using var factory = _site.In("Development");
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/health", UriKind.Relative));
        request.Headers.Add("Origin", origin);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(origin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task NobodyElseMayCallEvenInDevelopment()
    {
        using var factory = _site.In("Development");
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/health", UriKind.Relative));
        request.Headers.Add("Origin", "https://not-this-site.example");

        using var response = await client.SendAsync(request);

        // The request itself still succeeds -- CORS is enforced by the browser, not the
        // server -- but without the header the browser refuses to hand the body over.
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task ProductionSendsNoCorsHeadersAtAll()
    {
        // Same origin only. In production this API serves the built app itself, so there is
        // no legitimate cross-origin caller, and an open policy would let any site on the
        // internet run searches through this operator's Bungie key.
        using var factory = _site.In("Production");
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/health", UriKind.Relative));
        request.Headers.Add("Origin", "http://localhost:5173");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task APreflightIsAnsweredInDevelopmentAndIgnoredInProduction()
    {
        using var development = _site.In("Development");
        using var client = development.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Options, new Uri("/api/health", UriKind.Relative));
        request.Headers.Add("Origin", "http://localhost:5173");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(
            "http://localhost:5173",
            Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));

        using var production = _site.In("Production");
        using var productionClient = production.CreateClient();

        using var blocked = new HttpRequestMessage(HttpMethod.Options, new Uri("/api/health", UriKind.Relative));
        blocked.Headers.Add("Origin", "http://localhost:5173");
        blocked.Headers.Add("Access-Control-Request-Method", "GET");

        using var refused = await productionClient.SendAsync(blocked);

        Assert.False(refused.Headers.Contains("Access-Control-Allow-Origin"));
    }
}

/// <summary>
/// The Destiny API hosted in memory, on fixtures. Shared across the tests in a class so the
/// manifest is read once rather than once per assertion.
/// </summary>
public sealed class DestinySite : WebApplicationFactory<Program>
{
    /// <summary>The same app in a named environment, for the two halves of the CORS contract.</summary>
    public WebApplicationFactory<Program> In(string environment) =>
        WithWebHostBuilder(builder => builder.UseEnvironment(environment));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment(Environments.Development);
    }
}
