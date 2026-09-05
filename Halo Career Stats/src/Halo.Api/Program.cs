using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eet.Halo.Api;
using Eet.Halo.Client;
using Eet.Halo.Client.Endpoints;
using Eet.Trackers.Core;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

var haloOptions = new HaloOptions();
builder.Configuration.GetSection(HaloOptions.SectionName).Bind(haloOptions);

// ---------------------------------------------------------------------------
// Credentials.
//
// The tracker runs with none. HaloTrackerSetup serves fixtures unless an IXboxAuth has
// been registered in the container, and nothing below registers one, so a clean checkout
// starts and serves a complete synthetic career.
//
// To go live, register the Eet.Xbox implementation here, before AddHaloCareerSource:
//
//     builder.Services.AddSingleton<IXboxAuth, /* the Eet.Xbox implementation */>();
//
// That is the only change needed; everything downstream already distinguishes live from
// fixture and labels its output accordingly. This project deliberately does not reference
// Eet.Xbox, which is being built concurrently -- Halo.Client codes against IXboxAuth and
// nothing else.
// ---------------------------------------------------------------------------
var credentialHint = CredentialHints.Detect(builder.Configuration);

builder.Services.ConfigureHttpJsonOptions(options => ApiJson.Configure(options.SerializerOptions));
builder.Services.AddProblemDetails();
builder.Services.AddHaloCareerSource(haloOptions, builder.Environment.ContentRootPath);

var app = builder.Build();

// ---------------------------------------------------------------------------
// RFC 7807 for everything that escapes a handler. A TrackerException carries a Remedy --
// the thing the operator should actually do -- and that is what lands in `detail`, because
// "Unauthorized" without "your Spartan token expired, sign in again" is not an error
// message, it is a shrug.
// ---------------------------------------------------------------------------
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var problem = ApiProblems.From(error, context.Request.Path);
    context.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
    context.Response.ContentType = "application/problem+json";
    await context.Response.WriteAsJsonAsync(problem, ApiJson.Options, "application/problem+json");
}));

// ---------------------------------------------------------------------------
// Static files come from Career Stats Shared/web, which another agent is building. The
// directory may not exist yet and the app must start anyway, so this is entirely
// conditional -- a missing dashboard costs you the dashboard, not the API.
// ---------------------------------------------------------------------------
var webRoot = StaticAssets.Locate(app.Configuration["Halo:WebDirectory"] ?? "Career Stats Shared/web",
    app.Environment.ContentRootPath);

if (webRoot is not null)
{
    var files = new PhysicalFileProvider(webRoot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
    app.Logger.LogInformation("Serving the dashboard from {WebRoot}.", webRoot);
}
else
{
    app.Logger.LogInformation(
        "No dashboard directory found (looked for Career Stats Shared/web). The API is fully functional; only the UI is missing.");
}

var source = app.Services.GetRequiredService<HaloCareerSource>();
var endpoints = app.Services.GetRequiredService<HaloEndpointResolver>();

// Fail loudly at startup, not per-request, if 343 has re-published the manifest with
// different clearance rules than this client was written against.
endpoints.Validate();

// ---------------------------------------------------------------------------
// Routes
// ---------------------------------------------------------------------------

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    game = "halo-infinite",
    generatedAt = DateTimeOffset.UtcNow,
    source = source.IsFixture ? "fixtures" : "live",
    isFixture = source.IsFixture,
    manifestEndpoints = HaloEndpointManifest.Default.Count,
    credentials = credentialHint,
    conventions = new
    {
        durations = "TimeSpan values are serialised as a number of seconds.",
        rates = "Accuracy and win rate are fractions in [0,1], not percentages.",
        culture = "All formatted strings are culture-invariant; a K/D is \"1.42\", never \"1,42\".",
        enums = "Enums are serialised as strings.",
    },
}))
.WithName("Health");

app.MapGet("/api/player", async (string? q, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(q))
    {
        return ApiProblems.BadRequest(
            "A search query is required.",
            "Pass ?q= with a gamertag or an XUID. XUIDs are more reliable: a gamertag containing a non-Latin letter that renders like a Latin one cannot be typed and so cannot be searched.");
    }

    var player = await source.ResolveAsync(q, ct);
    return player is null
        ? ApiProblems.NotFound($"No player matches \"{q}\".", HaloPlayerQuery.NotFoundRemedy(q))
        : Results.Ok(new
        {
            player,
            typedQuery = q,
            // The homoglyph diagnosis, surfaced rather than buried. If the resolved handle
            // is not the string the user typed, say exactly why -- that is the difference
            // between a working search box and a dead end.
            homoglyphNotice = Identity.Explain(player.Handle),
            handleIsTypeable = !Identity.LooksLikeHomoglyph(player.Handle),
        });
})
.WithName("ResolvePlayer");

app.MapGet("/api/career", async (string? player, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(player))
    {
        return ApiProblems.BadRequest(
            "A player is required.",
            "Pass ?player= with an XUID, or resolve a gamertag first with /api/player?q=.");
    }

    var resolved = await source.ResolveAsync(player, ct);
    if (resolved is null)
    {
        return ApiProblems.NotFound($"No player matches \"{player}\".", HaloPlayerQuery.NotFoundRemedy(player));
    }

    return Results.Ok(await source.GetSnapshotAsync(resolved, ct));
})
.WithName("Career");

app.MapGet("/api/matches", async (string? player, int? count, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(player))
    {
        return ApiProblems.BadRequest(
            "A player is required.",
            "Pass ?player= with an XUID, or resolve a gamertag first with /api/player?q=.");
    }

    var resolved = await source.ResolveAsync(player, ct);
    if (resolved is null)
    {
        return ApiProblems.NotFound($"No player matches \"{player}\".", HaloPlayerQuery.NotFoundRemedy(player));
    }

    var matches = await source.GetMatchesAsync(resolved, count ?? 25, ct);
    return Results.Ok(new
    {
        player = resolved,
        count = matches.Count,
        isFixture = source.IsFixture,
        matches,
    });
})
.WithName("Matches");

// Only when there is no dashboard to serve at "/". With one, the static middleware above
// has already answered.
if (webRoot is null)
{
    app.MapGet("/", () => Results.Text(
        string.Create(
            CultureInfo.InvariantCulture,
            $"""
             halo-career-stats is running{(source.IsFixture ? " on SYNTHETIC FIXTURES (no credentials configured)" : " against live services")}.

             The dashboard is not present -- Career Stats Shared/web does not exist yet -- but the API works:

               GET /api/health
               GET /api/player?q=Elissu
               GET /api/career?player=2814669301245176
               GET /api/matches?player=2814669301245176&count=25
             """),
        "text/plain"));
}

await app.RunAsync();

/// <summary>Exposed so the test project can spin the app up in-process.</summary>
public partial class Program;
