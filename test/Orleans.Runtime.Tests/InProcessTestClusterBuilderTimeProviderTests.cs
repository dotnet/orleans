using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Runtime;
using Orleans.TestingHost;
using Xunit;

namespace Tester;

public class InProcessTestClusterBuilderTimeProviderTests
{
    [Fact, TestCategory("BVT")]
    public async Task ConfigureHost_CanReplaceTimeProvider_ForClientAndSiloServices()
    {
        var fakeTimeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-21T12:00:00+00:00"));
        var builder = new InProcessTestClusterBuilder();
        builder.ConfigureHost(hostBuilder =>
        {
            hostBuilder.Services.Replace(ServiceDescriptor.Singleton<TimeProvider>(fakeTimeProvider));
        });

        await using var cluster = builder.Build();
        await cluster.DeployAsync();

        Assert.Same(fakeTimeProvider, cluster.Client.ServiceProvider.GetRequiredService<TimeProvider>());
        Assert.Same(fakeTimeProvider, cluster.GetSiloServiceProvider().GetRequiredService<TimeProvider>());
        Assert.Same(fakeTimeProvider, cluster.GetSiloServiceProvider().GetRequiredService<IGrainRuntime>().TimeProvider);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
