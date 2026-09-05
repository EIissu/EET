using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Eet.Halo.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Eet.Halo.Tests;

/// <summary>
/// The whole app, over a real HTTP pipeline, in-process and without a socket.
///
/// Routing policy is the one thing that cannot be unit tested. Whether an unknown /api path
/// comes back as JSON or as the single-page app is decided by middleware order and endpoint
/// precedence, not by any function; the only honest way to assert it is to make the request.
/// No credentials are involved and none are needed -- the app serves fixtures.
/// </summary>
public sealed class SiteEndpointTests : IClassFixture<HaloSite>
{
    private readonly HaloSite _site;

    public SiteEndpointTests(HaloSite site) => _site = site;

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

        using var response = await client.GetAsync(new Uri("/api/player/extra/segments", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task AnUnknownPageIsTheAppSoADeepLinkSurvivesAReload()
    {
        // The site keeps its state in the query string today, and /?game=halo&q=Elissu works
        // because "/" is a real file. This is the promise for the day it uses paths instead.
        var front = WebAssets.Choose(WebAssets.SpaDirectory, WebAssets.VanillaDirectory, HaloSite.RepositoryRoot);
        Assert.NotNull(front);

        using var client = _site.CreateClient();

        using var response = await client.GetAsync(new Uri("/halo/Elissu/career", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("<", body, StringComparison.Ordinal);

        // index.html names the content-hashed bundles, so caching it breaks the next deploy.
        Assert.True(response.Headers.CacheControl?.NoCache);
    }

    [Fact]
    public async Task APageWhosePathMerelyStartsWithApiIsStillThePage()
    {
        // "/apiary" is not "/api". A prefix check rather than a segment check would answer
        // this with a JSON 404 and break a perfectly ordinary route.
        Assert.NotNull(WebAssets.Choose(WebAssets.SpaDirectory, WebAssets.VanillaDirectory, HaloSite.RepositoryRoot));

        using var client = _site.CreateClient();

        using var response = await client.GetAsync(new Uri("/apiary", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task TheRootServesAFrontEnd()
    {
        using var client = _site.CreateClient();

        using var response = await client.GetAsync(new Uri("/", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    // -----------------------------------------------------------------------------------
    // Search, as a person actually types it
    // -----------------------------------------------------------------------------------

    [Fact]
    public async Task TheLatinSpellingFindsTheGamertagThatCannotBeTyped()
    {
        using var client = _site.CreateClient();

        var found = await client.GetFromJsonAsync<JsonElement>(
            new Uri("/api/player?q=Elissu", UriKind.Relative));

        // The Player's own fields, at the top level. A caller that asked who this is should
        // get a player back rather than a player wrapped in a diagnostic envelope.
        Assert.Equal(TestEnv.Xuid, found.GetProperty("id").GetString());
        Assert.Equal(TestEnv.Gamertag, found.GetProperty("handle").GetString());
        Assert.Equal("Xbox", found.GetProperty("platform").GetString());

        Assert.Equal("homoglyph", found.GetProperty("matchedBy").GetString());
        Assert.False(found.GetProperty("handleIsTypeable").GetBoolean());

        // And the diagnosis names the code point, because "not found" would have been a lie
        // and "found something that looks like what you typed" needs explaining.
        Assert.Contains(
            "U+0415",
            found.GetProperty("homoglyphNotice").GetString()!,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("%20%20Elissu%20%20")]
    [InlineData("xuid(2814669301245176)")]
    [InlineData("XUID(2814669301245176)")]
    [InlineData("%202814669301245176%20")]
    public async Task WhatSomebodyPastedStillResolves(string raw)
    {
        using var client = _site.CreateClient();

        var found = await client.GetFromJsonAsync<JsonElement>(
            new Uri("/api/player?q=" + raw, UriKind.Relative));

        Assert.Equal(TestEnv.Xuid, found.GetProperty("id").GetString());
    }

    [Fact]
    public async Task TheDecoyIsNotTheSamePlayer()
    {
        // The fixture holds two profiles on purpose. Folding homoglyphs must not turn the
        // search into "close enough".
        using var client = _site.CreateClient();

        var decoy = await client.GetFromJsonAsync<JsonElement>(
            new Uri("/api/player?q=Elissu%20Decoy", UriKind.Relative));

        Assert.NotEqual(TestEnv.Xuid, decoy.GetProperty("id").GetString());
        Assert.Equal("exact", decoy.GetProperty("matchedBy").GetString());
    }

    [Fact]
    public async Task AMissAnswersWithSomethingAPersonCanActOn()
    {
        using var client = _site.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/api/player?q=NoSuchPlayerAnywhere", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var detail = document.RootElement.GetProperty("detail").GetString()!;

        // Not "player not found". The reason a correct-looking gamertag can never match.
        Assert.Contains("XUID", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AMissOnAGamertagThatIsNotTheTextItLooksLikeNamesTheCodePoint()
    {
        using var client = _site.CreateClient();

        // The Cyrillic spelling of a player who does not exist: Identity.Explain is the only
        // thing that can tell somebody why their eyes and the API disagree.
        using var response = await client.GetAsync(
            new Uri("/api/player?q=%D0%95lissuu", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains(
            "U+0415",
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
    // The one thing the site must never get wrong
    // -----------------------------------------------------------------------------------

    [Fact]
    public async Task HealthAndTheCareerBothSayTheDataIsSynthetic()
    {
        using var client = _site.CreateClient();

        var health = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/health", UriKind.Relative));
        Assert.True(health.GetProperty("isFixture").GetBoolean());

        var career = await client.GetFromJsonAsync<JsonElement>(
            new Uri("/api/career?player=xuid(" + TestEnv.Xuid + ")", UriKind.Relative));
        Assert.True(career.GetProperty("isFixture").GetBoolean());

        // And a duration is a number of seconds, which is what the site's types say.
        Assert.Equal(
            JsonValueKind.Number,
            career.GetProperty("totals").GetProperty("timePlayed").ValueKind);
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
        // no legitimate cross-origin caller, and an open one would let any site on the
        // internet run searches through this operator's Xbox credentials.
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
/// The Halo API hosted in memory. Shared across the tests in a class so the fixtures and the
/// endpoint manifest are read once rather than once per assertion.
/// </summary>
public sealed class HaloSite : WebApplicationFactory<Program>
{
    /// <summary>
    /// Where the front ends live, found the same way the app finds them: by walking up from
    /// the test binary until Career Stats Shared/fixtures appears.
    /// </summary>
    public static string RepositoryRoot { get; } =
        Directory.GetParent(TestEnv.FixtureDirectory)?.Parent?.FullName
        ?? AppContext.BaseDirectory;

    /// <summary>The same app in a named environment, for the two halves of the CORS contract.</summary>
    public WebApplicationFactory<Program> In(string environment) =>
        WithWebHostBuilder(builder => builder.UseEnvironment(environment));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment(Environments.Development);
    }
}
