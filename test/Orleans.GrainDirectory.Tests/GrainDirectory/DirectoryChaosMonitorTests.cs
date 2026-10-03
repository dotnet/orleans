using Microsoft.Extensions.DependencyInjection;
using Orleans.Concurrency;
using Orleans.Runtime.Diagnostics;
using Orleans.Runtime.GrainDirectory;
using Orleans.TestingHost;
using Xunit;

namespace UnitTests.GrainDirectory;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("GrainDirectory")]
[TestCategory("BVT")]
public sealed class DirectoryChaosMonitorTests
{
    [Fact]
    public async Task ActualPartitionInvariantEmitsEvidenceAndPreservesTheRpcException()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var monitor = new DirectoryChaosMonitor();
        var builder = new InProcessTestClusterBuilder(1);
#pragma warning disable ORLEANSEXP003
        builder.Options.UseDistributedGrainDirectory = true;
#pragma warning restore ORLEANSEXP003
        await using var cluster = builder.Build();
        await cluster.DeployAsync(cancellationToken);
        var silo = cluster.Silos[0];
        monitor.TrackSilo(silo.SiloAddress);
        var membership = silo.ServiceProvider.GetRequiredService<DirectoryMembershipService>();
        var version = silo.ServiceProvider.GetRequiredService<IClusterMembershipService>().CurrentSnapshot.Version;
        var view = await membership.RefreshViewAsync(version, cancellationToken);
        var factory = silo.ServiceProvider.GetRequiredService<IInternalGrainFactory>();
        var partition = factory.GetSystemTarget<IGrainDirectoryTestHooks>(
            GrainDirectoryPartition.CreateGrainId(silo.SiloAddress, 0).GrainId);
        await partition.WaitForMembershipVersionAsync(view.Version, cancellationToken);
        var missing = GrainAddress.NewActivationAddress(silo.SiloAddress, GrainId.Create("chaos", "missing"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => partition.CheckActivationsAsync(
                new Immutable<List<GrainAddress>>([missing]), cancellationToken).AsTask());

        Assert.True(monitor.InvariantFailure.IsCompletedSuccessfully);
        var evidence = await monitor.InvariantFailure;
        Assert.True(evidence.HasInvariantEvidence);
        Assert.Equal(exception.Message, evidence.InnerException!.Message);
        Assert.Contains(missing.GrainId.ToString(), evidence.Phase);
    }

    [Fact]
    public async Task ExplicitInvariantEvidenceIncludesPartitionContextAndOriginalFailure()
    {
        using var monitor = new DirectoryChaosMonitor();
        var silo = SiloAddress.FromParsableString("127.0.0.1:11111@123");
        var grain = GrainId.Create("chaos", "registration");
        var failure = new InvalidOperationException("Registration was lost.");
        monitor.TrackSilo(silo);
        monitor.RecordPhase("completed handoff");
        monitor.RecordSuccessfulBatch();

        GrainDirectoryEvents.EmitIntegrityViolation(silo, 3, new(7), RingRange.Full, grain, failure);

        var observed = await monitor.InvariantFailure.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(observed.HasInvariantEvidence);
        Assert.Same(failure, observed.InnerException);
        Assert.Contains("partition=3", observed.Phase);
        Assert.Contains("view=7", observed.Phase);
        Assert.Contains(grain.ToString(), observed.Phase);
        Assert.Contains("completed handoff", observed.Message);
        Assert.Contains("Successful workload batches: 1", observed.Message);
    }

    [Fact]
    public void AnotherClustersInvariantDoesNotContaminateThisRun()
    {
        using var monitor = new DirectoryChaosMonitor();
        monitor.TrackSilo(SiloAddress.FromParsableString("127.0.0.1:11111@123"));

        monitor.OnNext(new GrainDirectoryEvents.IntegrityViolation(
            SiloAddress.FromParsableString("127.0.0.1:22222@456"), 0, new(1), RingRange.Full,
            GrainId.Create("chaos", "foreign"), new InvalidOperationException("Other cluster.")));

        Assert.False(monitor.InvariantFailure.IsCompleted);
    }

    [Fact]
    public void UnexpectedRuntimeFailureRetainsAttributionWithoutClaimingAnInvariantViolation()
    {
        using var monitor = new DirectoryChaosMonitor();
        var failure = new InvalidOperationException("A different subsystem failed.");

        var observed = monitor.RuntimeFailure("starting silo", failure);

        Assert.False(observed.HasInvariantEvidence);
        Assert.Equal("starting silo", observed.Phase);
        Assert.Same(failure, observed.InnerException);
        Assert.Contains("Unclassified runtime/infrastructure failure", observed.Message);
        Assert.False(monitor.InvariantFailure.IsCompleted);
    }

    [Fact]
    public void AggregateRequiresEveryFailureToBeAnExpectedDisruption()
    {
        var unavailable = new SiloUnavailableException("Injected silo failure.");
        var unrelated = new InvalidOperationException("Unexpected runtime failure.");

        Assert.True(DirectoryChaosMonitor.IsExpectedDisruption(new AggregateException(unavailable, unavailable)));
        Assert.False(DirectoryChaosMonitor.IsExpectedDisruption(new AggregateException(unavailable, unrelated)));
        Assert.False(DirectoryChaosMonitor.IsExpectedDisruption(new OperationCanceledException()));
        Assert.True(DirectoryChaosMonitor.IsExpectedShutdown(new AggregateException(unavailable, new OperationCanceledException())));
        Assert.False(DirectoryChaosMonitor.IsExpectedShutdown(new AggregateException(unavailable, unrelated)));
    }
}
