using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Dashboard;
using Orleans.Dashboard.Core;
using Orleans.Dashboard.Implementation;
using Orleans.Hosting;
using Orleans.Services;
using Orleans.TestingHost;
using TestGrains;
using Xunit;

namespace UnitTests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Dashboard")]
public class SiloRuntimeStatisticsTests
{
    private static readonly MethodInfo CollectStatisticsMethod = typeof(SiloGrainService)
        .GetMethod("CollectStatistics", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact]
    public async Task CollectStatistics_ReportsConnectedClientsAndMessageBytes()
    {
        var builder = new InProcessTestClusterBuilder(1);
        builder.Options.ConfigureFileLogging = false;
        builder.ConfigureHost(host => host.Logging.ClearProviders());
        builder.ConfigureSilo((_, silo) => silo.AddDashboard(options => options.CounterUpdateIntervalMs = 60_000));
        await using var cluster = builder.Build();
        await cluster.DeployAsync(TestContext.Current.CancellationToken);
        var silo = Assert.Single(cluster.Silos);
        var service = silo.ServiceProvider.GetServices<IGrainService>().OfType<SiloGrainService>().Single();
        var client = silo.ServiceProvider.GetRequiredService<IDashboardClient>();
        var address = silo.SiloAddress.ToParsableString();

        await CollectStatistics(service, TestContext.Current.CancellationToken);
        var before = (await client.HistoricalStats(address, TestContext.Current.CancellationToken)).Value.Last()!;
        Assert.Equal(1, before.ClientCount);

        await cluster.Client.GetGrain<ITestStateInMemoryGrain>(0).GetState();
        await CollectStatistics(service, TestContext.Current.CancellationToken);
        var after = (await client.HistoricalStats(address, TestContext.Current.CancellationToken)).Value.Last()!;
        Assert.Equal(1, after.ClientCount);
        Assert.True(after.ReceivedMessages > before.ReceivedMessages);
        Assert.True(after.SentMessages > before.SentMessages);
    }

    private static Task CollectStatistics(SiloGrainService service, CancellationToken cancellationToken) =>
        (Task)CollectStatisticsMethod.Invoke(service, [true, cancellationToken])!;
}
