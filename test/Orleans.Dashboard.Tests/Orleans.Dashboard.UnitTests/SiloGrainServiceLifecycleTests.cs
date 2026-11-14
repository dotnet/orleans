using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Dashboard;
using Orleans.Dashboard.Implementation;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.Services;
using Orleans.TestingHost;
using Xunit;

namespace UnitTests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Dashboard")]
public class SiloGrainServiceLifecycleTests
{
    private static readonly MethodInfo CollectStatisticsMethod = typeof(SiloGrainService)
        .GetMethod("CollectStatistics", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact]
    public async Task Stop_DisposesStatisticsTimerBeforeServiceProviderDisposal()
    {
        var builder = new InProcessTestClusterBuilder(1);
        builder.Options.ConfigureFileLogging = false;
        builder.ConfigureHost(host => host.Logging.ClearProviders());
        builder.ConfigureSilo((_, silo) => silo
            .AddMemoryGrainStorageAsDefault()
            .AddDashboard(options => options.CounterUpdateIntervalMs = 60_000));
        await using var cluster = builder.Build();
        var observer = new StatisticsTimerObserver();
        using var subscription = GrainTimerEvents.AllEvents.Subscribe(observer);
        await cluster.DeployAsync(TestContext.Current.CancellationToken);
        var silo = Assert.Single(cluster.Silos);
        var service = silo.ServiceProvider.GetServices<IGrainService>().OfType<SiloGrainService>().Single();
        var timer = Assert.Single(observer.CreatedTimers);

        // Exercise the real management grain while the provider is alive.
        await CollectStatistics(service, TestContext.Current.CancellationToken);
        var statistics = (await service.GetRuntimeStatistics(TestContext.Current.CancellationToken)).Value;
        Assert.NotNull(statistics.Last());

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await CollectStatistics(service, cancellation.Token);
        Assert.Equal(statistics, (await service.GetRuntimeStatistics(TestContext.Current.CancellationToken)).Value);
        Assert.Empty(observer.DisposedTimers);

        await silo.StopSiloAsync(TestContext.Current.CancellationToken);

        // Stop, rather than DI disposal, must own timer cancellation.
        Assert.Same(timer, Assert.Single(observer.DisposedTimers));
        await service.Stop();
        Assert.Single(observer.DisposedTimers);
        silo.ServiceProvider.GetRequiredService<IGrainFactory>();

        await silo.DisposeAsync();
        await CollectStatistics(service, CancellationToken.None);
        Assert.Equal(statistics, (await service.GetRuntimeStatistics(TestContext.Current.CancellationToken)).Value);
    }

    [Fact]
    public async Task CollectStatistics_WhenProviderIsDisposed_DoesNotEscapeTimerCallback()
    {
        var host = CreateUnstartedHost();
        var service = host.Services.GetServices<IGrainService>().OfType<SiloGrainService>().Single();
        var factory = host.Services.GetRequiredService<IGrainFactory>();
        host.Dispose();
        Assert.Throws<ObjectDisposedException>(() => factory.GetGrain<IManagementGrain>(0));

        await CollectStatistics(service, CancellationToken.None);

        Assert.Empty((await service.GetRuntimeStatistics(TestContext.Current.CancellationToken)).Value);
    }

    private static IHost CreateUnstartedHost() => new HostBuilder()
        .ConfigureLogging(logging => logging.ClearProviders())
        .UseOrleans(silo => silo.UseLocalhostClustering().AddDashboard())
        .Build();

    private static Task CollectStatistics(SiloGrainService service, CancellationToken cancellationToken) =>
        (Task)CollectStatisticsMethod.Invoke(service, [true, cancellationToken])!;

    private sealed class StatisticsTimerObserver : IObserver<GrainTimerEvents.TimerEvent>
    {
        public List<IGrainTimer> CreatedTimers { get; } = [];
        public List<IGrainTimer> DisposedTimers { get; } = [];

        public void OnNext(GrainTimerEvents.TimerEvent value)
        {
            if (value.GrainContext.GrainInstance is not SiloGrainService)
            {
                return;
            }

            lock (CreatedTimers)
            {
                if (value is GrainTimerEvents.Created)
                {
                    CreatedTimers.Add(value.Timer);
                }
                else if (value is GrainTimerEvents.Disposed)
                {
                    DisposedTimers.Add(value.Timer);
                }
            }
        }

        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }
}
