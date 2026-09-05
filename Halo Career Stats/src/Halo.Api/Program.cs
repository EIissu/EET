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

// ---------------------------------------------------------------------------
// CORS, in development only, and narrowly.
//
// Production serves the built app from this same origin, so there is nothing legitimate to
// allow and the policy is simply not registered -- no headers, no preflight, no way for
// another site to spend this operator's Xbox credentials from a visitor's browser.
// AllowAnyOrigin would do exactly that, which is why it is not here.
//
// Development is the one case where the two halves genuinely live on different origins:
// Vite on :5173, this API on :5210. The dev proxy in vite.config.ts usually hides that, but
// it is one config change away from being bypassed and the failure it produces otherwise is
// a browser console message about a missing header, not anything about the app.
// ---------------------------------------------------------------------------
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddCors(cors => cors.AddPolicy(DevCors.PolicyName, policy => policy
        .WithOrigins([.. DevCors.Origins])
        .WithMethods([.. DevCors.Methods])
        .WithHeaders([.. DevCors.Headers])));
}

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
// Static files.
//
// Two front ends can be served and the choice is ordered, not preferential: the built React
// app in Career Stats Web/dist is the product, and the dependency-free dashboard in
// Career Stats Shared/web is what answers when nobody has run npm. The vanilla one is not
// legacy and is not going away -- it is the zero-build path, and it has to keep working.
//
// Either may be absent and the app must start anyway, so this is entirely conditional: a
// missing front end costs you the front end, not the API. Which one was picked is logged,
// because "why am I looking at the old dashboard" has exactly one answer and it should not
// take a debugger to find it.
// ---------------------------------------------------------------------------
if (app.Environment.IsDevelopment())
{
    app.UseCors(DevCors.PolicyName);
}

var web = WebAssets.Choose(
    app.Configuration["Halo:SpaDirectory"] ?? WebAssets.SpaDirectory,
    app.Configuration["Halo:WebDirectory"] ?? WebAssets.VanillaDirectory,
    app.Environment.ContentRootPath);

if (web is not null)
{
    var files = new PhysicalFileProvider(web.Root);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = files,
        OnPrepareResponse = served =>
        {
            // index.html names the content-hashed bundles, so a cached copy of it points at
            // files that a redeploy has already deleted. The bundles themselves are safe to
            // cache forever precisely because their names change when they do.
            if (string.Equals(served.File.Name, "index.html", StringComparison.OrdinalIgnoreCase))
            {
                served.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            }
        },
    });
    app.Logger.LogInformation(
        "Serving the {Front} from {WebRoot}.", web.Label, web.Root);
}
else
{
    app.Logger.LogInformation(
        "No front end found (looked for {Spa}, then {Vanilla}). The API is fully functional; only the UI is missing.",
        WebAssets.SpaDirectory,
        WebAssets.VanillaDirectory);
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
    // Whatever came out of a search box: trimmed, stripped of the invisible characters a
    // paste drags along, and unwrapped if it is an xuid(...) reference. Nothing here decides
    // what matches; it only stops a stray space deciding it.
    var query = SearchQuery.Normalize(q);

    if (query.Length == 0)
    {
        return ApiProblems.BadRequest(
            "A search query is required.",
            "Pass ?q= with a gamertag or an XUID. XUIDs are more reliable: a gamertag containing a non-Latin letter that renders like a Latin one cannot be typed and so cannot be searched.");
    }

    var player = await source.ResolveAsync(query, ct);
    if (player is null)
    {
        return ApiProblems.NotFound($"No player matches \"{query}\".", HaloPlayerQuery.NotFoundRemedy(query));
    }

    // The Player record's own fields, at the top level, because that is what this endpoint
    // is for: a caller asked "who is this" and should get a player back, not a player
    // wrapped in a diagnostic envelope it has to unpack. The diagnostics ride alongside.
    return Results.Ok(new
    {
        player.Handle,
        player.Id,
        player.Platform,
        player.IconUrl,
        isFixture = source.IsFixture,
        typedQuery = q,
        normalizedQuery = query,
        // How the typed text reached this player. "homoglyph" is the interesting one: the
        // query and the handle read identically to a human and share no bytes.
        matchedBy = string.Equals(player.Handle, query, StringComparison.OrdinalIgnoreCase)
            ? "exact"
            : Identity.LooksTheSame(player.Handle, query)
                ? "homoglyph"
                : "id",
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
    var query = SearchQuery.Normalize(player);

    if (query.Length == 0)
    {
        return ApiProblems.BadRequest(
            "A player is required.",
            "Pass ?player= with an XUID, or resolve a gamertag first with /api/player?q=.");
    }

    var resolved = await source.ResolveAsync(query, ct);
    if (resolved is null)
    {
        return ApiProblems.NotFound($"No player matches \"{query}\".", HaloPlayerQuery.NotFoundRemedy(query));
    }

    return Results.Ok(await source.GetSnapshotAsync(resolved, ct));
})
.WithName("Career");

app.MapGet("/api/matches", async (string? player, int? count, CancellationToken ct) =>
{
    var query = SearchQuery.Normalize(player);

    if (query.Length == 0)
    {
        return ApiProblems.BadRequest(
            "A player is required.",
            "Pass ?player= with an XUID, or resolve a gamertag first with /api/player?q=.");
    }

    var resolved = await source.ResolveAsync(query, ct);
    if (resolved is null)
    {
        return ApiProblems.NotFound($"No player matches \"{query}\".", HaloPlayerQuery.NotFoundRemedy(query));
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

// Only when there is no front end to serve at "/". With one, the static middleware above
// has already answered.
if (web is null)
{
    app.MapGet("/", () => Results.Text(
        string.Create(
            CultureInfo.InvariantCulture,
            $"""
             halo-career-stats is running{(source.IsFixture ? " on SYNTHETIC FIXTURES (no credentials configured)" : " against live services")}.

             No front end is present -- neither Career Stats Web/dist nor Career Stats Shared/web -- but the API works:

               GET /api/health
               GET /api/player?q=Elissu
               GET /api/career?player=2814669301245176
               GET /api/matches?player=2814669301245176&count=25
             """),
        "text/plain"));
}

// ---------------------------------------------------------------------------
// The fallback, which has two jobs and must not confuse them.
//
// A single-page app owns its own URLs. Today the site puts its state in the query string --
// /?game=halo&q=Elissu -- and that already works, because "/" is a real file. The moment it
// adopts paths, /halo/Elissu is a request the server has never heard of, and answering 404
// would break every bookmark and every reload. So an unknown path outside /api gets
// index.html and the app routes it client-side.
//
// The other job is refusing to do that for /api. An unknown API route answered with HTML
// and a 200 is a genuinely nasty bug: the caller's fetch() succeeds, JSON.parse dies on
// "Unexpected token <", and nothing in that message hints that the path was wrong. Unknown
// API routes stay JSON 404s with a body that says what does exist.
// ---------------------------------------------------------------------------
app.MapFallback(async context =>
{
    if (ApiRoutes.IsApi(context.Request.Path))
    {
        var problem = ApiProblems.UnknownApiRoute(context.Request.Path);
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(problem, ApiJson.Options, "application/problem+json");
        return;
    }

    if (web is null)
    {
        // Nothing to fall back to. A 404 is the honest answer, and the "/" route above has
        // already explained where the API is.
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    context.Response.StatusCode = StatusCodes.Status200OK;
    context.Response.ContentType = "text/html; charset=utf-8";
    context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
    await context.Response.SendFileAsync(web.IndexPath);
});

await app.RunAsync();

/// <summary>Exposed so the test project can spin the app up in-process.</summary>
public partial class Program;
