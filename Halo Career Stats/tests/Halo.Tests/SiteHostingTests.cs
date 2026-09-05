using Eet.Halo.Api;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Eet.Halo.Tests;

/// <summary>
/// The decisions the API makes about serving a site rather than about Halo: which front end
/// wins, what counts as an API path, who is allowed to call cross-origin, and what a search
/// box's contents mean once the invisible characters are taken off them.
///
/// These are pure functions on purpose. The HTTP behaviour they add up to is asserted
/// separately, over a real pipeline, in <see cref="SiteEndpointTests"/>.
/// </summary>
public sealed class SiteHostingTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "eet-web-assets-" + Guid.NewGuid().ToString("N"));

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
        var spa = Make(WebAssets.SpaDirectory, withIndex: true);
        Make(WebAssets.VanillaDirectory, withIndex: true);

        var choice = WebAssets.Choose(WebAssets.SpaDirectory, WebAssets.VanillaDirectory, _root);

        Assert.NotNull(choice);
        Assert.Equal(WebAssetKind.Spa, choice.Kind);
        Assert.Equal(Path.GetFullPath(spa), Path.GetFullPath(choice.Root));
    }

    [Fact]
    public void TheVanillaDashboardAnswersWhenNobodyHasRunNpm()
    {
        // The zero-dependency path is not a fallback in the sense of being second best. It
        // is the one that works on a clean checkout, and it has to keep working.
        var vanilla = Make(WebAssets.VanillaDirectory, withIndex: true);

        var choice = WebAssets.Choose(WebAssets.SpaDirectory, WebAssets.VanillaDirectory, _root);

        Assert.NotNull(choice);
        Assert.Equal(WebAssetKind.Vanilla, choice.Kind);
        Assert.Equal(Path.GetFullPath(vanilla), Path.GetFullPath(choice.Root));
    }

    [Fact]
    public void AnEmptyDistDirectoryDoesNotOutrankAWorkingDashboard()
    {
        // A dist/ left behind by an interrupted build, or made by hand, is a directory and
        // nothing else. Preferring it would serve 404s from a site that was working a moment
        // ago, and the log line would claim the React app was being served.
        Make(WebAssets.SpaDirectory, withIndex: false);
        Make(WebAssets.VanillaDirectory, withIndex: true);

        var choice = WebAssets.Choose(WebAssets.SpaDirectory, WebAssets.VanillaDirectory, _root);

        Assert.NotNull(choice);
        Assert.Equal(WebAssetKind.Vanilla, choice.Kind);
    }

    [Fact]
    public void NoFrontEndAtAllIsNullRatherThanAFailureToStart()
    {
        Assert.Null(WebAssets.Choose(WebAssets.SpaDirectory, WebAssets.VanillaDirectory, _root));
    }

    [Fact]
    public void TheChoiceIsLabelledForTheStartupLog()
    {
        Make(WebAssets.SpaDirectory, withIndex: true);
        var choice = WebAssets.Choose(WebAssets.SpaDirectory, WebAssets.VanillaDirectory, _root);

        Assert.NotNull(choice);
        Assert.Contains("React", choice.Label, StringComparison.Ordinal);
        Assert.True(File.Exists(choice.IndexPath));
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
    [InlineData("/halo/Elissu", false)]
    // The one that a naive StartsWith("/api") gets wrong. A page called "apiary" is a page.
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

        // A tracker whose whole point is that the browser holds no credential must not
        // become a way for another site to spend the operator's.
        Assert.All(DevCors.Origins, origin => Assert.StartsWith("http://", origin, StringComparison.Ordinal));

        // Read-only API. Nothing here should ever need a method that changes something.
        Assert.Equal(new[] { "GET", "HEAD", "OPTIONS" }, DevCors.Methods);
    }

    // -----------------------------------------------------------------------------------
    // What the person typed
    // -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("Elissu", "Elissu")]
    [InlineData("  Elissu  ", "Elissu")]
    [InlineData("\t Elissu \r\n", "Elissu")]
    // Copying a gamertag out of a web page brings these along, and they match nothing.
    // Written as escapes, never as literals: see TestEnv.Gamertag for why every source file
    // in this tracker is pure ASCII.
    [InlineData("\u200BElissu\u200B", "Elissu")]
    [InlineData("\uFEFF Elissu", "Elissu")]
    [InlineData("\u200EElissu\u200F", "Elissu")]
    // The form this API prints back at you, which is therefore the form people paste.
    [InlineData("xuid(2814669301245176)", "2814669301245176")]
    [InlineData("XUID(2814669301245176)", "2814669301245176")]
    [InlineData(" xuid( 2814669301245176 ) ", "2814669301245176")]
    [InlineData("2814669301245176", "2814669301245176")]
    // A gamertag is whatever a person registered, including one shaped like the wrapper.
    // Unwrapping that would search for something nobody asked for.
    [InlineData("xuid(hello)", "xuid(hello)")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void ASearchQueryLosesOnlyWhatNoIdentifierEverContains(string? raw, string expected) =>
        Assert.Equal(expected, SearchQuery.Normalize(raw));

    [Fact]
    public void NormalisingNeverTouchesTheLettersThatMakeAHomoglyphAHomoglyph()
    {
        // Folding is Identity's job and happens at match time. If normalisation quietly
        // ASCII-fied the query here, the API could no longer tell an exact match from a
        // homoglyph one, and the diagnosis a person needs would disappear.
        const string cyrillic = TestEnv.Gamertag;

        Assert.Equal(cyrillic, SearchQuery.Normalize("  " + cyrillic + "  "));
    }

    // -----------------------------------------------------------------------------------
    // The unknown-route problem document
    // -----------------------------------------------------------------------------------

    [Fact]
    public void AnUnknownApiRouteProblemNamesThePathAndTheRoutesThatExist()
    {
        var problem = ApiProblems.UnknownApiRoute("/api/nope");

        Assert.Equal(StatusCodes.Status404NotFound, problem.Status);
        Assert.Contains("/api/nope", problem.Title!, StringComparison.Ordinal);
        Assert.Contains("/api/player", problem.Detail!, StringComparison.Ordinal);
        Assert.Equal("/api/nope", problem.Instance);
    }
}
