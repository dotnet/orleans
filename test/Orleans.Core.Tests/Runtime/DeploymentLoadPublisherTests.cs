using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Core.Diagnostics;
using Orleans.Runtime;
using Orleans.Runtime.Dissemination;
using Orleans.Runtime.Scheduler;
using Orleans.Serialization;
using Orleans.Statistics;
using TestExtensions;
using Xunit;

namespace NonSilo.Tests.Runtime;

[TestCategory("BVT"), TestCategory("Placement")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public class DeploymentLoadPublisherTests
{
    [Theory]
    [InlineData(-5000)]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task PublishStatistics_NonPositiveRefreshInterval_PreservesDirectPublication(int refreshMilliseconds)
    {
        using var rig = CreateTestRig(TimeSpan.FromMilliseconds(refreshMilliseconds));

        await rig.Publisher.PublishStatistics(TestContext.Current.CancellationToken);

        var statistics = rig.Publisher.LocalRuntimeStatistics;
        await rig.DirectTarget.Received(1).UpdateRuntimeStatistics(
            rig.LocalSilo, statistics, TestContext.Current.CancellationToken);
        Assert.Same(statistics, rig.Publisher.PeriodicStatistics[rig.LocalSilo]);
        Assert.Empty(rig.Dissemination.ReceivedCalls());
    }

    [Fact]
    public async Task PublishStatistics_DisseminationCancelsIndependently_FallsBackToDirectPublication()
    {
        using var rig = CreateTestRig(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        rig.Dissemination.Publish(
                Arg.Any<IDisseminationNamespace>(),
                Arg.Any<DisseminationKey>(),
                Arg.Any<long>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromCanceled<bool>(cancellation.Token));

        await rig.Publisher.PublishStatistics(TestContext.Current.CancellationToken);

        await rig.DirectTarget.Received(1).UpdateRuntimeStatistics(
            rig.LocalSilo, rig.Publisher.LocalRuntimeStatistics, TestContext.Current.CancellationToken);
        await rig.Dissemination.Received(1).Publish(
            Arg.Any<IDisseminationNamespace>(),
            rig.LocalSilo,
            rig.Publisher.LocalRuntimeStatistics.DateTime.Ticks,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishStatistics_CallerCancels_DoesNotFallBackToDirectPublication()
    {
        using var rig = CreateTestRig(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        rig.Dissemination.Publish(
                Arg.Any<IDisseminationNamespace>(),
                Arg.Any<DisseminationKey>(),
                Arg.Any<long>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                cancellation.Cancel();
                return ValueTask.FromCanceled<bool>(call.ArgAt<CancellationToken>(3));
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => rig.Publisher.PublishStatistics(cancellation.Token));

        Assert.Empty(rig.DirectTarget.ReceivedCalls());
    }

    private static TestRig CreateTestRig(TimeSpan refreshTime)
    {
        var localSilo = SiloAddress.FromParsableString("127.0.0.1:100@100");
        var remoteSilo = SiloAddress.FromParsableString("127.0.0.1:200@100");
        var localDetails = Substitute.For<ILocalSiloDetails>();
        localDetails.SiloAddress.Returns(localSilo);
        var statusOracle = Substitute.For<ISiloStatusOracle>();
        statusOracle.GetApproximateSiloStatus(Arg.Any<SiloAddress>()).Returns(SiloStatus.Active);
        statusOracle.GetApproximateSiloStatuses(onlyActive: true).Returns(new Dictionary<SiloAddress, SiloStatus>
        {
            [localSilo] = SiloStatus.Active,
            [remoteSilo] = SiloStatus.Active,
        });
        var directTarget = Substitute.For<IDeploymentLoadPublisher>();
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        grainFactory.GetSystemTarget<IDeploymentLoadPublisher>(
            Constants.DeploymentLoadPublisherSystemTargetType, remoteSilo).Returns(directTarget);
        var dissemination = Substitute.For<IDisseminationService>();
        dissemination.Publish(
                Arg.Any<IDisseminationNamespace>(),
                Arg.Any<DisseminationKey>(),
                Arg.Any<long>(),
                Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(true));

        var services = new ServiceCollection();
        services.AddSerializer();
        services.AddMetrics();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<TimeProvider>(new FakeTimeProvider());
        services.AddSingleton<OrleansInstruments>();
        services.AddSingleton<CatalogInstruments>();
        services.AddSingleton<SchedulerInstruments>();
        services.AddSingleton<GrainInstruments>();
        services.AddSingleton<MessagingInstruments>();
        services.AddSingleton<MessagingProcessingInstruments>();
        services.AddSingleton(localDetails);
        services.AddSingleton(statusOracle);
        services.AddSingleton(grainFactory);
        services.AddSingleton(dissemination);
        services.AddSingleton(Substitute.For<IActivationWorkingSet>());
        services.AddSingleton(Substitute.For<IEnvironmentStatisticsProvider>());
        services.AddSingleton<IOptions<LoadSheddingOptions>>(Options.Create(new LoadSheddingOptions()));
        services.AddOptions<DeploymentLoadPublisherOptions>().Configure(options =>
        {
            options.DeploymentLoadPublisherRefreshTime = refreshTime;
            options.Dissemination.Enabled = true;
        });
        services.AddSingleton<ActivationDirectory>();
        services.AddSingleton(serviceProvider => new SystemTargetShared(
            runtimeClient: null!,
            localDetails,
            NullLoggerFactory.Instance,
            Options.Create(new SchedulingOptions()),
            grainReferenceActivator: null!,
            timerRegistry: null!,
            serviceProvider.GetRequiredService<ActivationDirectory>(),
            serviceProvider.GetRequiredService<SchedulerInstruments>(),
            serviceProvider.GetRequiredService<GrainInstruments>(),
            serviceProvider.GetRequiredService<MessagingInstruments>(),
            serviceProvider.GetRequiredService<MessagingProcessingInstruments>()));
        services.AddSingleton<DeploymentLoadPublisher>();
        services.AddSingleton<DeploymentLoadStatisticsDisseminationNamespace>();
        var serviceProvider = services.BuildServiceProvider();
        return new(
            serviceProvider,
            serviceProvider.GetRequiredService<DeploymentLoadPublisher>(),
            dissemination,
            directTarget,
            localSilo);
    }

    private sealed record TestRig(
        ServiceProvider ServiceProvider,
        DeploymentLoadPublisher Publisher,
        IDisseminationService Dissemination,
        IDeploymentLoadPublisher DirectTarget,
        SiloAddress LocalSilo) : IDisposable
    {
        public void Dispose() => ServiceProvider.Dispose();
    }
}
