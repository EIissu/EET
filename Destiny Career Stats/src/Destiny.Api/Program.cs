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
    json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

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
// The dashboard is built by another agent into Career Stats Shared/web, and may not exist yet.
// A missing directory is not an error: the API is useful on its own and must start cleanly
// either way.
// ---------------------------------------------------------------------------------------
var webRoot = SharedWeb.Find();
if (webRoot is not null)
{
    var files = new PhysicalFileProvider(webRoot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
}

// ---------------------------------------------------------------------------------------
// API
// ---------------------------------------------------------------------------------------

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    game = nameof(GameId.Destiny2),
    // The one thing an operator needs at a glance: is this real data or is it fixtures.
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
    web = new { root = webRoot, present = webRoot is not null },
    // Never the key itself, only whether one is configured.
    configuration = options.Describe(),
    utc = DateTimeOffset.UtcNow,
}));

app.MapGet("/api/player", async (string? q, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(q))
    {
        return DestinyProblem.BadRequest(
            "Missing query",
            "Pass ?q= with a Bungie name such as Guardian#1234, or a Destiny membership id.");
    }

    return await DestinyProblem.GuardAsync(async () =>
    {
        var player = await tracker.Career.ResolveAsync(q, ct).ConfigureAwait(false);
        return player is null
            ? DestinyProblem.NotFound(
                "Player not found",
                $"Nothing on Bungie.net matches \"{q}\".",
                "Bungie names are exact, including the four digits after the hash. If the display "
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
    if (!string.IsNullOrWhiteSpace(membershipId))
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

    return string.IsNullOrWhiteSpace(q)
        ? null
        : await tracker.Career.ResolveAsync(q, ct).ConfigureAwait(false);
}

static IResult MissingIdentity(string? q) => DestinyProblem.BadRequest(
    "Missing player",
    string.IsNullOrWhiteSpace(q)
        ? "Pass ?membershipType= and ?membershipId=, or ?q= with a Bungie name such as Guardian#1234."
        : string.Create(CultureInfo.InvariantCulture, $"Could not resolve \"{q}\" to a Destiny player."));

/// <summary>Named so tests and tooling have something to reference.</summary>
public partial class Program;
