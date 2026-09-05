using System.Globalization;
using System.Text.Json.Serialization;
using Eet.Destiny.Api;
using Eet.Destiny.Client;
using Eet.Trackers.Core;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------------------
// Configuration
//
// Three sources, in ascending priority: appsettings.json (committed, and holds nothing
// secret), appsettings.Development.json (gitignored, where a developer's key goes), and the
// environment. BUNGIE_API_KEY is spelled out as well as the Bungie__ApiKey form because it
// is the name every other Destiny tool uses and guessing it wrong is a bad first five
// minutes.
// ---------------------------------------------------------------------------------------
var options = builder.Configuration.GetSection("Bungie").Get<BungieOptions>() ?? new BungieOptions();

options.ApiKey = FirstNonEmpty(
    options.ApiKey,
    builder.Configuration["Bungie:ApiKey"],
    Environment.GetEnvironmentVariable("BUNGIE_API_KEY"));

options.FixtureDirectory = FirstNonEmpty(options.FixtureDirectory, FixtureLocator.Find());

builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(json =>
{
    // Enums as strings. Better.Higher and GameId.Destiny2 mean something to whoever reads
    // the payload; 0 and 1 do not, and the numbering is an implementation detail of an enum
    // in a shared assembly that either game's client could reorder.
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter());

    // Durations as a number of seconds, which is what the shared conventions say and what
    // the Halo tracker has always done. The framework default writes a TimeSpan as
    // "24.22:30:00"; one game switcher over two APIs that disagree about that is how a
    // career page ends up showing NaN hours played.
    json.SerializerOptions.Converters.Add(new TimeSpanSecondsConverter());

    json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

// ---------------------------------------------------------------------------------------
// CORS, in development only, and narrowly.
//
// Production serves the built app from this same origin, so there is nothing legitimate to
// allow and the policy is simply never registered. AllowAnyOrigin would let any site on the
// internet run searches through this operator's Bungie key from a visitor's browser, which
// is why it is not here.
//
// Development is the one case where the two halves genuinely live on different origins:
// Vite on :5173, this API on :5231. The dev proxy in vite.config.ts usually hides that, but
// it is one config change away from being bypassed, and the failure that produces is a
// browser console message about a missing header rather than anything about the app.
// ---------------------------------------------------------------------------------------
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddCors(cors => cors.AddPolicy(DevCors.PolicyName, policy => policy
        .WithOrigins([.. DevCors.Origins])
        .WithMethods([.. DevCors.Methods])
        .WithHeaders([.. DevCors.Headers])));
}

DestinyTracker tracker;
try
{
    tracker = DestinyTracker.Create(options);
}
catch (TrackerException ex)
{
    // Failing to start is worse than starting useless, but starting with a bare stack trace
    // is worse than both. This is the one place where the message has to carry the fix.
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine(ex.Remedy);
    return 1;
}

builder.Services.AddSingleton(tracker);
builder.Services.AddSingleton(options);

var app = builder.Build();
app.Lifetime.ApplicationStopped.Register(tracker.Dispose);

// Everything this API expects to go wrong is caught by DestinyProblem and answered with a
// remedy. Anything that is not expected still has to answer with something: without this,
// an unhandled exception leaves Kestrel to send a bare 500 with an empty body, which tells
// a caller nothing at all and is indistinguishable from the process having died. Paired
// with AddProblemDetails above, this turns it into RFC 7807. The exception is still logged.
app.UseExceptionHandler();

// ---------------------------------------------------------------------------------------
// Static files
//
// Two front ends can be served and the order is not a preference: the built React app in
// Career Stats Web/dist is the product, and the dependency-free dashboard in
// Career Stats Shared/web is what answers when nobody has run npm. The vanilla one is not
// legacy -- it is the zero-build path and it has to keep working.
//
// Either may be absent and the API must start cleanly regardless: a missing front end costs
// you the front end, not the API. Which one was chosen is logged, because "why am I looking
// at the old dashboard" should not take a debugger to answer.
// ---------------------------------------------------------------------------------------
if (app.Environment.IsDevelopment())
{
    app.UseCors(DevCors.PolicyName);
}

var web = WebAssets.Choose();
var webRoot = web?.Root;
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
            // files a redeploy has already deleted. The bundles themselves are safe to cache
            // forever precisely because their names change when they do.
            if (string.Equals(served.File.Name, "index.html", StringComparison.OrdinalIgnoreCase))
            {
                served.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            }
        },
    });

    app.Logger.LogInformation("Serving the {Front} from {WebRoot}.", web.Label, web.Root);
}
else
{
    app.Logger.LogInformation(
        "No front end found (looked for {Spa}, then {Vanilla}). The API is fully functional; only the UI is missing.",
        WebAssets.SpaDirectory,
        WebAssets.VanillaDirectory);
}

// ---------------------------------------------------------------------------------------
// API
// ---------------------------------------------------------------------------------------

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    game = "destiny-2",
    // The one thing an operator needs at a glance: is this real data or is it fixtures.
    //
    // `isFixture` is the field that matters and it is spelled the same way here as in the
    // Halo tracker's health endpoint and in every CareerSnapshot. Two APIs that share a
    // career model should not each invent their own way of saying which mode they are in;
    // CI asserts this exact key, because the zero-credential path is the one promise this
    // project makes to somebody who has not registered anything yet.
    isFixture = tracker.IsFixture,
    mode = tracker.IsFixture ? "fixture" : "live",
    source = tracker.IsFixture ? "fixture" : "bungie.net",
    fixtureDirectory = tracker.FixtureDirectory,
    definitions = new
    {
        loaded = tracker.Definitions.IsLoaded,
        version = tracker.Definitions.Version,
        cachePath = tracker.Definitions.CachePath,
        fromCache = tracker.Definitions.LoadedFromCache,
    },
    web = new { root = webRoot, present = web is not null, kind = web?.Kind.ToString() },
    // Never the key itself, only whether one is configured.
    configuration = options.Describe(),
    utc = DateTimeOffset.UtcNow,
}));

app.MapGet("/api/player", async (string? q, CancellationToken ct) =>
{
    // Whatever came out of a search box: trimmed, stripped of the invisible characters a
    // paste drags along, and unwrapped if it is an xuid(...) reference from the Halo half of
    // the site. Nothing here decides what matches; it only stops a stray space deciding it.
    var query = SearchQuery.Normalize(q);

    if (query.Length == 0)
    {
        return DestinyProblem.BadRequest(
            "Missing query",
            "Pass ?q= with a Bungie name such as Guardian#1234, or a Destiny membership id.");
    }

    return await DestinyProblem.GuardAsync(async () =>
    {
        var player = await tracker.Career.ResolveAsync(query, ct).ConfigureAwait(false);
        return player is null
            ? DestinyProblem.NotFound(
                "Player not found",
                string.Create(CultureInfo.InvariantCulture, $"Nothing on Bungie.net matches \"{query}\"."),
                // Identity.Explain names the offending code points when the text typed is not
                // the text it appears to be, which is the one failure a person cannot debug by
                // looking harder at their own screen.
                Identity.Explain(query)
                ?? "Bungie names are exact, including the four digits after the hash. If the display "
                + "name uses characters that only look like Latin letters, search by membership id "
                + "instead.")
            : Results.Ok(player);
    }).ConfigureAwait(false);
});

app.MapGet("/api/career", async (
    string? membershipType, string? membershipId, string? q, CancellationToken ct) =>
    await DestinyProblem.GuardAsync(async () =>
    {
        var player = await ResolvePlayerAsync(tracker, membershipType, membershipId, q, ct)
            .ConfigureAwait(false);

        if (player is null)
        {
            return MissingIdentity(q);
        }

        var snapshot = await tracker.Career.GetSnapshotAsync(player, ct).ConfigureAwait(false);
        return Results.Ok(snapshot);
    }).ConfigureAwait(false));

app.MapGet("/api/matches", async (
    string? membershipType, string? membershipId, string? q, int? count, CancellationToken ct) =>
    await DestinyProblem.GuardAsync(async () =>
    {
        var player = await ResolvePlayerAsync(tracker, membershipType, membershipId, q, ct)
            .ConfigureAwait(false);

        if (player is null)
        {
            return MissingIdentity(q);
        }

        var matches = await tracker.Career
            .GetMatchesAsync(player, count ?? 25, ct)
            .ConfigureAwait(false);

        return Results.Ok(new
        {
            player,
            game = nameof(GameId.Destiny2),
            isFixture = tracker.IsFixture,
            count = matches.Count,
            matches,
        });
    }).ConfigureAwait(false));

// ---------------------------------------------------------------------------------------
// The fallback, which has two jobs and must not confuse them.
//
// A single-page app owns its own URLs. Today the site keeps its state in the query string --
// /?game=destiny&q=Guardian%231234 -- and that already works, because "/" is a real file.
// The moment it adopts paths, /destiny/Guardian is a request this server has never heard of,
// and a 404 would break every bookmark and every reload. So an unknown path outside /api
// gets index.html and the app routes it client-side.
//
// The other job is refusing to do that for /api. An unknown API route answered with HTML and
// a 200 is a genuinely nasty bug: the caller's fetch() succeeds, JSON.parse dies on
// "Unexpected token <", and nothing in that message hints that the path was wrong. Unknown
// API routes stay JSON 404s with a body that says what does exist.
// ---------------------------------------------------------------------------------------
app.MapFallback(async context =>
{
    if (ApiRoutes.IsApi(context.Request.Path))
    {
        await DestinyProblem
            .UnknownApiRoute(context.Request.Path)
            .ExecuteAsync(context)
            .ConfigureAwait(false);
        return;
    }

    if (web is null)
    {
        // Nothing to fall back to. A 404 is the honest answer.
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    context.Response.StatusCode = StatusCodes.Status200OK;
    context.Response.ContentType = "text/html; charset=utf-8";
    context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
    await context.Response.SendFileAsync(web.IndexPath).ConfigureAwait(false);
});

app.Run();
return 0;

// ---------------------------------------------------------------------------------------

static string? FirstNonEmpty(params string?[] candidates) =>
    candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));

/// <summary>
/// A (membershipType, membershipId) pair is what Bungie keys on, so that is the documented
/// route. Accepting ?q= as well costs nothing and saves a round trip for a caller that only
/// has a Bungie name.
/// </summary>
static async Task<Player?> ResolvePlayerAsync(
    DestinyTracker tracker, string? membershipType, string? membershipId, string? q, CancellationToken ct)
{
    membershipId = SearchQuery.Normalize(membershipId);

    if (membershipId.Length > 0)
    {
        var type = string.IsNullOrWhiteSpace(membershipType)
            ? BungieMembershipType.Steam
            : BungieMembershipType.TryParse(membershipType, out var parsed)
                ? parsed
                : throw new TrackerException(
                    $"\"{membershipType}\" is not a Destiny membership type.",
                    "Use a number (1 Xbox, 2 PlayStation, 3 Steam, 4 Blizzard, 5 Stadia, 6 Epic "
                    + "Games) or the platform name. All (-1) works only on the player search.");

        return new Player(
            membershipId.Trim(),
            membershipId.Trim(),
            BungieMembershipType.Name(type));
    }

    var query = SearchQuery.Normalize(q);
    return query.Length == 0
        ? null
        : await tracker.Career.ResolveAsync(query, ct).ConfigureAwait(false);
}

static IResult MissingIdentity(string? q) => DestinyProblem.BadRequest(
    "Missing player",
    string.IsNullOrWhiteSpace(q)
        ? "Pass ?membershipType= and ?membershipId=, or ?q= with a Bungie name such as Guardian#1234."
        : string.Create(CultureInfo.InvariantCulture, $"Could not resolve \"{q}\" to a Destiny player."));

/// <summary>Named so tests and tooling have something to reference.</summary>
public partial class Program;
