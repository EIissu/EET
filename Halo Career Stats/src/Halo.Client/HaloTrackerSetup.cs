using Eet.Halo.Client.Endpoints;
using Eet.Halo.Client.Http;
using Eet.Trackers.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Eet.Halo.Client;

/// <summary>
/// Builds the whole Halo stack, live or fixture, and decides which of the two to build.
///
/// The decision is the important part, and it has exactly one rule: the tracker serves
/// fixtures unless an <see cref="IXboxAuth"/> has been registered. No credentials means no
/// token chain means no live data, and rather than failing at the first request, the app
/// starts, serves a complete synthetic career, and says so on every response. Somebody with
/// no API keys can run this today; the same binary switches to live data the moment an
/// auth implementation is registered, with no other change.
/// </summary>
public static class HaloTrackerSetup
{
    /// <summary>
    /// Register the Halo career source.
    /// </summary>
    /// <param name="contentRoot">Where to start looking for the fixture directory.</param>
    /// <remarks>
    /// Register your <see cref="IXboxAuth"/> BEFORE calling this. Its presence in the
    /// container is the signal that live data is possible.
    /// </remarks>
    public static IServiceCollection AddHaloCareerSource(
        this IServiceCollection services,
        HaloOptions options,
        string contentRoot)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton(sp => new HaloEndpointResolver(HaloEndpointManifest.Default));

        var live = !options.ForceFixtures && HasUsableAuth(services);
        if (live)
        {
            AddLive(services, options);
        }
        else
        {
            AddFixtures(services, options, contentRoot);
        }

        services.TryAddSingleton<HaloClient>();
        services.TryAddSingleton<ICareerSource, HaloCareerSource>();
        services.TryAddSingleton<HaloCareerSource>();
        return services;
    }

    /// <summary>
    /// Is there an <see cref="IXboxAuth"/> that can actually reach Xbox Live?
    ///
    /// The obvious test -- "is one registered" -- has a trap in it. The Eet.Xbox factory
    /// returns a FIXTURE auth when no client id is configured, which is the sensible default
    /// for that project but poison for this decision: a fixture token registered as
    /// IXboxAuth would flip this tracker into live mode and send invented credentials at
    /// 343's servers, turning a working zero-credential install into a wall of 401s.
    ///
    /// So a registration whose implementation type is itself a fixture does not count as
    /// credentials. Matching on the type name is a heuristic and deliberately a
    /// conservative one: it can only ever send us to fixtures, which is the safe direction,
    /// and this project cannot reference Eet.Xbox to do it properly by type.
    /// </summary>
    internal static bool HasUsableAuth(IServiceCollection services)
    {
        var registration = services.LastOrDefault(d => d.ServiceType == typeof(IXboxAuth));
        if (registration is null)
        {
            return false;
        }

        var implementation = registration.ImplementationType
            ?? registration.ImplementationInstance?.GetType();

        // A factory registration is opaque. Assume the caller meant it: they had to write
        // code to get here, whereas the fixture case above happens by default.
        return implementation is null
            || !implementation.Name.StartsWith("Fixture", StringComparison.Ordinal);
    }

    private static void AddFixtures(IServiceCollection services, HaloOptions options, string contentRoot)
    {
        var directory = FixtureHaloTransport.Locate(options.FixtureDirectory, contentRoot)
            ?? throw new TrackerException(
                $"No Xbox credentials are configured and the fixture directory '{options.FixtureDirectory}' could not be found from '{contentRoot}'.",
                "The tracker falls back to fixtures when it has no credentials, so it needs them to exist. They live in Career Stats Shared/fixtures; set Halo:FixtureDirectory to an absolute path if you are running from somewhere unusual.");

        services.TryAddSingleton(_ => new FixtureHaloTransport(directory));
        services.TryAddSingleton<IHaloTransport>(sp => sp.GetRequiredService<FixtureHaloTransport>());
        services.TryAddSingleton<IHaloPlayerDirectory>(sp =>
            new FixturePlayerDirectory(sp.GetRequiredService<FixtureHaloTransport>()));
    }

    private static void AddLive(IServiceCollection services, HaloOptions options)
    {
        services.TryAddSingleton<IHaloClearanceProvider>(sp => new SettingsClearanceProvider(
            // Clearance is fetched through a transport whose auth handler does not itself
            // ask for clearance. Anything else is a cycle: you cannot need the header to
            // fetch the value of the header.
            BuildTransport(sp, options, new NoClearanceProvider()),
            sp.GetRequiredService<HaloEndpointResolver>(),
            options,
            sp.GetService<ILogger<SettingsClearanceProvider>>()));

        services.TryAddSingleton<IHaloTransport>(sp => BuildTransport(
            sp,
            options,
            sp.GetRequiredService<IHaloClearanceProvider>()));

        services.TryAddSingleton<IHaloPlayerDirectory>(sp => new XboxProfilePlayerDirectory(
            Identify(
                new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
                {
                    Timeout = TimeSpan.FromSeconds(20),
                },
                options),
            sp.GetRequiredService<IXboxAuth>()));
    }

    private static HttpHaloTransport BuildTransport(
        IServiceProvider sp,
        HaloOptions options,
        IHaloClearanceProvider clearance) =>
        new(
            CreateHttpClient(
                options,
                sp.GetRequiredService<IXboxAuth>(),
                clearance,
                sp.GetService<ILoggerFactory>()),
            sp.GetService<ILogger<HttpHaloTransport>>());

    /// <summary>
    /// The live handler chain, outermost first.
    ///
    ///   disk cache  -> a hit costs no token, no retry budget and no concurrency slot
    ///   resilience  -> retry with backoff, honouring Retry-After
    ///   concurrency -> the politeness cap, inside retry so a sleeping request frees its slot
    ///   auth        -> Spartan always, 343-clearance only where the manifest says so
    ///
    /// Built once and held, rather than through IHttpClientFactory, because the concurrency
    /// cap has to be one semaphore for the life of the process to mean anything. The
    /// pooled-connection lifetime covers the DNS staleness the factory would otherwise
    /// handle.
    /// </summary>
    public static HttpClient CreateHttpClient(
        HaloOptions options,
        IXboxAuth auth,
        IHaloClearanceProvider clearance,
        ILoggerFactory? loggerFactory = null,
        HttpMessageHandler? primary = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var inner = primary ?? new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };

        var chain = Wrap(new HaloAuthHandler(auth, clearance), inner);
        chain = Wrap(new HaloConcurrencyHandler(options.MaxConcurrentRequests), chain);
        chain = Wrap(
            new HaloResilienceHandler(options, loggerFactory?.CreateLogger<HaloResilienceHandler>()),
            chain);
        chain = Wrap(
            new HaloDiskCacheHandler(
                options.CacheDirectory ?? HaloDiskCacheHandler.DefaultDirectory,
                options.HistoryCacheLifetime,
                loggerFactory?.CreateLogger<HaloDiskCacheHandler>()),
            chain);

        return Identify(
            new HttpClient(chain, disposeHandler: true)
            {
                Timeout = TimeoutFor(options),
            },
            options);
    }

    /// <summary>Time allowed for the network part of every attempt put together.</summary>
    private static readonly TimeSpan AttemptAllowance = TimeSpan.FromSeconds(60);

    /// <summary>Nothing may hang a dashboard for longer than this, whatever the configuration says.</summary>
    private static readonly TimeSpan TimeoutCeiling = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The client timeout, sized so that it can actually contain the retry schedule.
    ///
    /// HttpClient.Timeout covers the WHOLE call -- every attempt and every backoff sleep
    /// between them -- so a fixed 60 seconds silently contradicts the defaults it sits
    /// next to: a throttled request honouring four Retry-After headers clamped to
    /// MaxRetryDelay spends 80 seconds asleep and is killed part-way through a schedule
    /// the operator explicitly configured. Worse, it dies reporting a timeout rather than
    /// the 429 the service was actually sending, so the one diagnostic that would explain
    /// it is thrown away. Sizing the timeout from the budget keeps the two consistent by
    /// construction; the ceiling keeps a careless configuration from hanging a page for an
    /// hour.
    /// </summary>
    internal static TimeSpan TimeoutFor(HaloOptions options)
    {
        var retries = Math.Max(0, options.MaxRetries);
        var longestSleep = options.MaxRetryDelay > TimeSpan.Zero ? options.MaxRetryDelay : TimeSpan.Zero;
        var total = AttemptAllowance + (longestSleep * retries);
        return total > TimeoutCeiling ? TimeoutCeiling : total;
    }

    /// <summary>
    /// Say who we are.
    ///
    /// HttpClient sends no User-Agent whatsoever unless told to, which is both rude on an
    /// undocumented API we are guests on and indistinguishable from a scraper. Naming the
    /// tool means 343 can block this client specifically if it ever misbehaves, rather than
    /// the address range it happens to share with everybody else.
    /// </summary>
    private static HttpClient Identify(HttpClient http, HaloOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.UserAgent))
        {
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", options.UserAgent);
        }

        return http;
    }

    private static DelegatingHandler Wrap(DelegatingHandler outer, HttpMessageHandler inner)
    {
        outer.InnerHandler = inner;
        return outer;
    }
}
