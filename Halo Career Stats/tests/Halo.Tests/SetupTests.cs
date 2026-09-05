using Eet.Halo.Client;
using Eet.Halo.Client.Http;
using Eet.Trackers.Core;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Eet.Halo.Tests;

/// <summary>
/// The live-versus-fixture decision. Getting this wrong in either direction is bad, but the
/// directions are not symmetric: choosing fixtures when live was possible shows synthetic
/// numbers clearly labelled as synthetic, while choosing live when it was not possible
/// breaks the zero-credential guarantee outright.
/// </summary>
public sealed class SetupTests
{
    [Fact]
    public void WithNoAuthRegisteredTheTrackerServesFixtures()
    {
        var services = Configured();

        Assert.False(HaloTrackerSetup.HasUsableAuth(services));

        using var provider = services.BuildServiceProvider();
        Assert.True(provider.GetRequiredService<ICareerSource>().IsFixture);
        Assert.IsType<FixtureHaloTransport>(provider.GetRequiredService<IHaloTransport>());
    }

    [Fact]
    public void ARealAuthImplementationSwitchesItToLive()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IXboxAuth, FakeXboxAuth>();

        Assert.True(HaloTrackerSetup.HasUsableAuth(services));
    }

    [Fact]
    public void AFixtureAuthDoesNotCountAsCredentials()
    {
        // The trap this guard exists for: Eet.Xbox's factory hands back a fixture auth when
        // no client id is configured. Treating that as credentials would send invented
        // tokens at 343 and break the zero-credential path.
        var services = new ServiceCollection();
        services.AddSingleton<IXboxAuth, FixtureOnlyAuth>();

        Assert.False(HaloTrackerSetup.HasUsableAuth(services));

        var configured = Configured(s => s.AddSingleton<IXboxAuth, FixtureOnlyAuth>());
        using var provider = configured.BuildServiceProvider();
        Assert.True(provider.GetRequiredService<ICareerSource>().IsFixture);
    }

    [Fact]
    public void AnInstanceRegistrationIsInspectedToo()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IXboxAuth>(new FixtureOnlyAuth());

        Assert.False(HaloTrackerSetup.HasUsableAuth(services));
    }

    [Fact]
    public void ForceFixturesBeatsEvenRealCredentials()
    {
        var services = Configured(
            s => s.AddSingleton<IXboxAuth, FakeXboxAuth>(),
            o => o.ForceFixtures = true);

        using var provider = services.BuildServiceProvider();
        Assert.True(provider.GetRequiredService<ICareerSource>().IsFixture);
    }

    private static IServiceCollection Configured(
        Action<IServiceCollection>? register = null,
        Action<HaloOptions>? configure = null)
    {
        var services = new ServiceCollection();
        register?.Invoke(services);

        var options = new HaloOptions { CacheDirectory = string.Empty };
        configure?.Invoke(options);

        return services.AddHaloCareerSource(options, AppContext.BaseDirectory);
    }

    /// <summary>Named to match the convention the guard keys on.</summary>
    private sealed class FixtureOnlyAuth : IXboxAuth
    {
        public Task<XstsToken> GetXstsTokenAsync(string relyingParty, CancellationToken ct = default) =>
            Task.FromResult(new XstsToken("f", "f", DateTimeOffset.UtcNow.AddHours(1), null));

        public Task<SpartanToken> GetSpartanTokenAsync(CancellationToken ct = default) =>
            Task.FromResult(new SpartanToken("f", DateTimeOffset.UtcNow.AddHours(1)));
    }
}
