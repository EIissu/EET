using Eet.Destiny.Client;
using Xunit;

namespace Eet.Destiny.Tests;

/// <summary>
/// The definition cache. Two things matter here: that only the two tables the tracker needs
/// are downloaded -- never the world content file -- and that a second run with an unchanged
/// manifest version reads from disk instead of the network.
/// </summary>
public sealed class ManifestCacheTests : IDisposable
{
    private const string ActivityPath = "/common/destiny2_content/json/en/DestinyActivityDefinition-abc.json";
    private const string ModePath = "/common/destiny2_content/json/en/DestinyActivityModeDefinition-abc.json";

    private readonly string _cache = Path.Combine(
        Path.GetTempPath(), "eet-destiny-tests", Guid.NewGuid().ToString("N"));

    private static string Manifest(string version) => Envelopes.Success($$"""
        {
          "version": "{{version}}",
          "jsonWorldContentPaths": {"en": "/common/destiny2_content/json/en/aggregate.json"},
          "jsonWorldComponentContentPaths": {
            "en": {
              "DestinyActivityDefinition": "{{ActivityPath}}",
              "DestinyActivityModeDefinition": "{{ModePath}}",
              "DestinyInventoryItemDefinition": "/common/destiny2_content/json/en/items.json"
            }
          }
        }
        """);

    private const string ActivityTable = """
        {
          "_note": "SYNTHETIC. A key that is not a hash must be skipped, not crash the parse.",
          "3628169985": {
            "displayProperties": {"name": "Rusted Lands", "icon": "/img/rusted.png", "hasIcon": true},
            "isPvP": true,
            "directActivityModeType": 10,
            "hash": 3628169985
          },
          "1102201127": {
            "displayProperties": {"name": "The Inverted Spire", "hasIcon": false},
            "isPvP": false,
            "directActivityModeType": 3,
            "hash": 1102201127
          },
          "4000000000": {
            "displayProperties": {"description": "no name at all"},
            "hash": 4000000000
          }
        }
        """;

    private const string ModeTable = """
        {
          "_note": "SYNTHETIC",
          "1000000010": {
            "displayProperties": {"name": "Control"},
            "modeType": 10,
            "activityModeCategory": 2,
            "isAggregateMode": false,
            "hash": 1000000010
          },
          "1000000005": {
            "displayProperties": {"name": "All PvP"},
            "modeType": 5,
            "activityModeCategory": 2,
            "isAggregateMode": true,
            "hash": 1000000005
          }
        }
        """;

    private (DestinyManifestCache Cache, StubHandler Handler, BungieOptions Options) Build(
        string version = "v1", string? cacheDirectory = null)
    {
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path switch
            {
                var p when p.EndsWith("/Manifest/", StringComparison.Ordinal) =>
                    StubHandler.Ok(Manifest(version)),
                ActivityPath => StubHandler.Ok(ActivityTable),
                ModePath => StubHandler.Ok(ModeTable),
                _ => StubHandler.Status(System.Net.HttpStatusCode.NotFound, "{}"),
            };
        });

        var options = new BungieOptions { CacheDirectory = cacheDirectory ?? _cache };
        var http = new HttpClient(handler) { BaseAddress = new Uri(options.PlatformBaseUrl) };
        var api = new BungieApiClient(http, options, (_, _) => Task.CompletedTask);
        return (new DestinyManifestCache(api, options), handler, options);
    }

    [Fact]
    public async Task Only_the_two_needed_tables_are_downloaded()
    {
        var (cache, handler, _) = Build();

        var warning = await cache.LoadAsync();

        Assert.Null(warning);
        Assert.Equal("Rusted Lands", cache.ActivityName(3628169985));
        Assert.True(cache.ActivityIsPvp(3628169985));
        Assert.False(cache.ActivityIsPvp(1102201127));
        Assert.Equal("Control", cache.ModeName(10));

        // The world content blob and the item table are both listed in the manifest and both
        // deliberately left alone; the item table alone is tens of megabytes.
        Assert.DoesNotContain(handler.Requests, r => r.Contains("aggregate.json", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, r => r.Contains("items.json", StringComparison.Ordinal));
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task A_key_that_is_not_a_hash_is_skipped_rather_than_breaking_the_parse()
    {
        // The fixtures carry a _note marking them synthetic, and it sits right alongside the
        // hash keys.
        var (cache, _, _) = Build();

        Assert.Null(await cache.LoadAsync());
        Assert.Equal("The Inverted Spire", cache.ActivityName(1102201127));
    }

    [Fact]
    public async Task A_definition_with_no_name_is_left_out()
    {
        var (cache, _, _) = Build();
        await cache.LoadAsync();

        Assert.Null(cache.ActivityName(4000000000));
    }

    [Fact]
    public async Task A_second_run_at_the_same_version_reads_the_tables_off_disk()
    {
        var (first, firstHandler, _) = Build();
        await first.LoadAsync();
        Assert.False(first.LoadedFromCache);
        Assert.Equal(3, firstHandler.CallCount);

        // A fresh cache object, as a restarted process would have.
        var (second, secondHandler, _) = Build();
        var warning = await second.LoadAsync();

        Assert.Null(warning);
        Assert.True(second.LoadedFromCache);
        Assert.Equal("Rusted Lands", second.ActivityName(3628169985));

        // The manifest is still checked -- it is the only way to notice a new version -- but
        // the tables behind it are not fetched again.
        Assert.Equal(1, secondHandler.CallCount);
        Assert.DoesNotContain(secondHandler.Requests, r => r.Contains("DestinyActivityDefinition", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_new_manifest_version_refetches_the_tables()
    {
        var (first, _, _) = Build(version: "v1");
        await first.LoadAsync();

        var (second, secondHandler, _) = Build(version: "v2");
        await second.LoadAsync();

        Assert.False(second.LoadedFromCache);
        Assert.Equal("v2", second.Version);
        Assert.Contains(secondHandler.Requests, r => r.Contains("DestinyActivityDefinition", StringComparison.Ordinal));

        // The old version's directory is pruned, so the cache does not grow without bound.
        var versions = Directory.GetDirectories(Path.Combine(_cache, "manifest"));
        Assert.Equal("v2", Path.GetFileName(Assert.Single(versions)));
    }

    [Fact]
    public async Task A_manifest_that_cannot_be_read_downgrades_to_built_in_labels()
    {
        // A tracker that cannot name a map is mildly worse. A tracker that refuses to show a
        // career because a CDN file went missing is useless.
        var handler = StubHandler.Always(
            Envelopes.Failure(BungiePlatformError.SystemDisabled, "SystemDisabled", "Maintenance"));

        var options = new BungieOptions { CacheDirectory = _cache };
        using var http = new HttpClient(handler) { BaseAddress = new Uri(options.PlatformBaseUrl) };
        var cache = new DestinyManifestCache(
            new BungieApiClient(http, options, (_, _) => Task.CompletedTask), options);

        var warning = await cache.LoadAsync();

        Assert.NotNull(warning);
        Assert.Contains("definitions", warning, StringComparison.OrdinalIgnoreCase);
        Assert.False(cache.IsLoaded);

        // The built-in table still names every mode a player is likely to have played.
        Assert.Equal("Iron Banner Control", cache.ModeName(43));
        Assert.Equal("Trials of Osiris", cache.ModeName(84));
        Assert.Equal(2, cache.ModeCategory(10));
        Assert.True(cache.IsAggregateMode(5));
    }

    [Fact]
    public async Task An_unknown_locale_falls_back_to_english_rather_than_coming_back_empty()
    {
        var (cache, _, options) = Build();
        options.Locale = "kr";

        // "en" is the documented fallback, so this still succeeds -- the point is that it
        // does not throw and does not come back empty.
        Assert.Null(await cache.LoadAsync());
        Assert.True(cache.IsLoaded);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_cache))
            {
                Directory.Delete(_cache, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }
}
