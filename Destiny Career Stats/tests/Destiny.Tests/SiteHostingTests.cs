using Eet.Destiny.Api;
using Eet.Trackers.Core;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Eet.Destiny.Tests;

/// <summary>
/// The decisions this API makes about serving a site rather than about Destiny: which front
/// end wins, what counts as an API path, who may call cross-origin, and what a search box's
/// contents mean once the invisible characters are taken off them.
///
/// Pure functions on purpose. The HTTP behaviour they add up to is asserted separately, over
/// a real pipeline, in <see cref="SiteEndpointTests"/>.
/// </summary>
public sealed class SiteHostingTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "eet-destiny-web-" + Guid.NewGuid().ToString("N"));

    private string Make(string relative, bool withIndex)
    {
        var dir = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(dir);
        if (withIndex)
        {
            File.WriteAllText(Path.Combine(dir, "index.html"), "<!doctype html><title>x</title>");
        }

        return dir;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // -----------------------------------------------------------------------------------
    // Which front end
    // -----------------------------------------------------------------------------------

    [Fact]
    public void TheBuiltAppWinsWhenBothFrontEndsArePresent()
    {
        Make(WebAssets.SpaDirectory, withIndex: true);
        Make(WebAssets.VanillaDirectory, withIndex: true);

        var choice = WebAssets.Choose(_root);

        Assert.NotNull(choice);
        Assert.Equal(WebAssetKind.Spa, choice.Kind);
        Assert.Contains("dist", choice.Root, StringComparison.Ordinal);
    }

    [Fact]
    public void TheVanillaDashboardAnswersWhenNobodyHasRunNpm()
    {
        // The zero-dependency path is not second best; it is the one that works on a clean
        // checkout, and it has to keep working.
        Make(WebAssets.VanillaDirectory, withIndex: true);

        var choice = WebAssets.Choose(_root);

        Assert.NotNull(choice);
        Assert.Equal(WebAssetKind.Vanilla, choice.Kind);
    }

    [Fact]
    public void AnEmptyDistDirectoryDoesNotOutrankAWorkingDashboard()
    {
        // A dist/ left behind by an interrupted build is a directory and nothing else.
        // Preferring it would serve 404s from a site that worked a moment ago.
        Make(WebAssets.SpaDirectory, withIndex: false);
        Make(WebAssets.VanillaDirectory, withIndex: true);

        var choice = WebAssets.Choose(_root);

        Assert.NotNull(choice);
        Assert.Equal(WebAssetKind.Vanilla, choice.Kind);
    }

    [Fact]
    public void NoFrontEndAtAllIsNullRatherThanAFailureToStart()
    {
        Assert.Null(WebAssets.Choose(_root));
        Assert.Null(SharedWeb.Find(_root));
        Assert.Null(SharedWeb.Locate(WebAssets.SpaDirectory, _root));
    }

    [Fact]
    public void TheOldFinderStillFindsTheOldDashboard()
    {
        // SharedWeb.Find is the name the rest of the app and its tests already use. It now
        // delegates to the general walk, and must keep answering exactly as it did.
        var vanilla = Make(WebAssets.VanillaDirectory, withIndex: true);

        Assert.Equal(Path.GetFullPath(vanilla), Path.GetFullPath(SharedWeb.Find(_root)!));
    }

    // -----------------------------------------------------------------------------------
    // What counts as an API path
    // -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("/api", true)]
    [InlineData("/api/", true)]
    [InlineData("/api/health", true)]
    [InlineData("/api/nope", true)]
    [InlineData("/API/NOPE", true)]
    [InlineData("/", false)]
    [InlineData("/destiny/Guardian", false)]
    // The one a naive StartsWith("/api") gets wrong. A page called "apiary" is a page.
    [InlineData("/apiary", false)]
    [InlineData("/api-docs", false)]
    public void ApiPathsAreRecognisedBySegmentAndNotByPrefix(string path, bool expected) =>
        Assert.Equal(expected, ApiRoutes.IsApi(new PathString(path)));

    // -----------------------------------------------------------------------------------
    // CORS
    // -----------------------------------------------------------------------------------

    [Fact]
    public void TheDevPolicyNamesTheViteOriginsExactlyAndNeverAWildcard()
    {
        Assert.Equal(new[] { "http://localhost:5173", "http://127.0.0.1:5173" }, DevCors.Origins);
        Assert.DoesNotContain("*", DevCors.Origins);
        Assert.All(DevCors.Origins, origin => Assert.StartsWith("http://", origin, StringComparison.Ordinal));

        // Read-only API. Nothing here should ever need a method that changes something.
        Assert.Equal(new[] { "GET", "HEAD", "OPTIONS" }, DevCors.Methods);
    }

    // -----------------------------------------------------------------------------------
    // What the person typed
    // -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("AnaGuardian#4412", "AnaGuardian#4412")]
    [InlineData("  AnaGuardian#4412  ", "AnaGuardian#4412")]
    [InlineData("\t AnaGuardian#4412 \r\n", "AnaGuardian#4412")]
    // Copying a Bungie name out of a web page brings these along, and they match nothing.
    // Written as escapes, never as literals: every source file here is pure ASCII so that a
    // cp1252 machine cannot mangle the characters these tests are about.
    [InlineData("\u200BAnaGuardian#4412\u200B", "AnaGuardian#4412")]
    [InlineData("\uFEFF AnaGuardian#4412", "AnaGuardian#4412")]
    // The site has one search box and a game switcher, so a Halo reference lands in the
    // Destiny box regularly. Unwrapped it fails as "no such membership id", which is true
    // and actionable; wrapped it fails as "that is not a Bungie name", which blames the
    // format rather than the game.
    [InlineData("xuid(4611686018400119004)", "4611686018400119004")]
    [InlineData(" xuid( 4611686018400119004 ) ", "4611686018400119004")]
    [InlineData("4611686018400119004", "4611686018400119004")]
    // A display name is whatever a person registered, including one shaped like the wrapper.
    [InlineData("xuid(hello)", "xuid(hello)")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void ASearchQueryLosesOnlyWhatNoIdentifierEverContains(string? raw, string expected) =>
        Assert.Equal(expected, SearchQuery.Normalize(raw));

    [Fact]
    public void NormalisingNeverTouchesTheLettersThatMakeAHomoglyphAHomoglyph()
    {
        // Folding is Identity's job. If normalisation quietly ASCII-fied the query, the API
        // could no longer tell a real name from one that only looks like it, and the
        // diagnosis a person needs would disappear.
        const string cyrillic = "\u0406lissu#9007";

        Assert.Equal(cyrillic, SearchQuery.Normalize("  " + cyrillic + "  "));
        Assert.True(Identity.LooksLikeHomoglyph(SearchQuery.Normalize(cyrillic)));
    }

    // -----------------------------------------------------------------------------------
    // Problem documents a person can act on
    // -----------------------------------------------------------------------------------

    [Fact]
    public void AnUnknownApiRouteProblemNamesThePathAndTheRoutesThatExist()
    {
        var problem = Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult>(
            DestinyProblem.UnknownApiRoute("/api/nope"));

        Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
        Assert.Contains("/api/nope", problem.ProblemDetails.Detail!, StringComparison.Ordinal);
        Assert.Contains("/api/player", problem.ProblemDetails.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void ANotFoundCarriesTheFixInDetailAndNotOnlyInTheExtension()
    {
        // Every generic RFC 7807 client, this site's own included, shows `detail`. A remedy
        // that lives only in an extension is a remedy nobody reads.
        var notFound = new TrackerException(
            "Bungie has no player called Ilissu#9007.",
            "Check the four-digit code; it changes when a player renames.");
        notFound.Data["httpStatus"] = StatusCodes.Status404NotFound;

        var problem = Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult>(
            DestinyProblem.From(notFound));

        Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
        Assert.Contains("Bungie has no player", problem.ProblemDetails.Detail!, StringComparison.Ordinal);
        Assert.Contains("four-digit code", problem.ProblemDetails.Detail!, StringComparison.Ordinal);

        // Still in the extension too, so a UI can style the fix apart from the failure.
        Assert.Equal(notFound.Remedy, problem.ProblemDetails.Extensions["remedy"]);
    }

    [Fact]
    public void ARemedyIsNotRepeatedWhenTheMessageAlreadyCarriesIt()
    {
        var duplicated = new TrackerException("Do the thing.", "Do the thing.");

        var problem = Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult>(
            DestinyProblem.From(duplicated));

        Assert.Equal("Do the thing.", problem.ProblemDetails.Detail);
    }
}
