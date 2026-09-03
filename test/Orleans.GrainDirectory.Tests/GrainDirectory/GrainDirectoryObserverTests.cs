using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.MembershipService;
using Orleans.TestingHost;
using Xunit;

namespace UnitTests.GrainDirectory;

[TestSuite("Functional")]
[TestProvider("None")]
[TestArea("TestingHost")]
[TestCategory("Functional")]
public sealed class GrainDirectoryObserverTests
{
    private const int PartitionCount = 3;
    private static readonly TimeSpan ConvergenceTimeout = TimeSpan.FromSeconds(30);
    private static readonly RingRange OperationRange = RingRange.FromPoint(0x1234_5678);

    [Fact]
    public async Task WaitForConvergenceAsync_ReturnsTrueOnlyAfterLocalAndEveryTargetPartitionAreObserved()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var target = await CreateTargetAsync(cancellationToken);
        await using var cluster = target.Cluster;
        using var observer = CreateObserver();

        var waiter = observer.WaitForConvergenceAsync(cluster.Silos, ConvergenceTimeout);
        Assert.False(
            waiter.IsCompleted,
            $"Convergence waiter for {target.SiloAddress} version {target.Version} should be armed before observations.");

        var observedPartitionCount = ObserveAllTargetPartitions(observer, target, target.Version);

        Assert.Equal(target.PartitionCount, observedPartitionCount);
        Assert.False(await observer.WaitForConvergenceAsync(cluster.Silos, TimeSpan.Zero));
        Assert.False(
            waiter.IsCompleted,
            $"Convergence waiter for {target.SiloAddress} should remain pending without the local version.");

        observer.OnNext(new GrainDirectoryEvents.MembershipVersionApplied(
            target.SiloAddress,
            new MembershipVersion(target.Version.Value - 1)));

        Assert.False(await observer.WaitForConvergenceAsync(cluster.Silos, TimeSpan.Zero));
        Assert.False(
            waiter.IsCompleted,
            $"Convergence waiter for {target.SiloAddress} should remain pending with a stale local version.");

        observer.OnNext(new GrainDirectoryEvents.MembershipVersionApplied(target.SiloAddress, target.Version));

        Assert.True(await AwaitConvergenceAsync(waiter, target, "the target local version observation", cancellationToken));
    }

    [Fact]
    public async Task WaitForConvergenceAsync_ReturnsFalseUntilEveryActiveSiloHasConverged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var targets = await CreateTargetsAsync(siloCount: 2, cancellationToken);
        Assert.Equal(2, targets.Length);
        await using var cluster = targets[0].Cluster;
        using var observer = CreateObserver();
        var waiter = observer.WaitForConvergenceAsync(cluster.Silos, ConvergenceTimeout);
        Assert.False(
            waiter.IsCompleted,
            $"Convergence waiter should be armed for targets {targets[0].SiloAddress} and {targets[1].SiloAddress}.");

        ObserveAllTargetVersions(observer, targets[0]);

        Assert.False(await observer.WaitForConvergenceAsync(cluster.Silos, TimeSpan.Zero));
        Assert.False(
            waiter.IsCompleted,
            $"Convergence of {targets[0].SiloAddress} must not hide missing observations for {targets[1].SiloAddress}.");

        ObserveAllTargetVersions(observer, targets[1]);

        Assert.True(await AwaitConvergenceAsync(
            waiter,
            targets[1],
            $"all active silos after {targets[0].SiloAddress} converged first",
            cancellationToken));
    }

    [Fact]
    public async Task WaitForConvergenceAsync_DuplicateIdenticalRangeStartsRequireOneMatchingCompletion()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var target = await CreateTargetAsync(cancellationToken);
        await using var cluster = target.Cluster;
        using var observer = CreateObserver();
        ObserveAllTargetVersions(observer, target);
        var started = CreateRangeOperationStarted(target.SiloAddress, target.Version);

        observer.OnNext(started);
        observer.OnNext(CreateRangeOperationStarted(target.SiloAddress, target.Version));

        Assert.False(await observer.WaitForConvergenceAsync(cluster.Silos, TimeSpan.Zero));

        var waiter = observer.WaitForConvergenceAsync(cluster.Silos, ConvergenceTimeout);
        Assert.False(
            waiter.IsCompleted,
            $"Duplicate pending range operation for {target.SiloAddress} should block convergence.");

        var mismatchedCompletions = new[]
        {
            (
                Field: "partition index",
                Event: CreateRangeOperationCompleted(started, partitionIndex: started.PartitionIndex + 1)),
            (
                Field: "membership version",
                Event: CreateRangeOperationCompleted(
                    started,
                    version: new MembershipVersion(started.Version.Value + 1))),
            (
                Field: "range",
                Event: CreateRangeOperationCompleted(started, range: RingRange.FromPoint(0x8765_4321))),
            (
                Field: "operation name",
                Event: CreateRangeOperationCompleted(
                    started,
                    operationName: GrainDirectoryEvents.ReleaseOperationName)),
        };

        foreach (var mismatch in mismatchedCompletions)
        {
            observer.OnNext(mismatch.Event);
            Assert.False(await observer.WaitForConvergenceAsync(cluster.Silos, TimeSpan.Zero));
            Assert.False(
                waiter.IsCompleted,
                $"A completion with the wrong {mismatch.Field} should not clear the pending operation for {target.SiloAddress}.");
        }

        observer.OnNext(CreateRangeOperationCompleted(started));

        Assert.True(await AwaitConvergenceAsync(waiter, target, "matching range-operation completion", cancellationToken));
    }

    [Fact]
    public async Task WaitForConvergenceAsync_ReturnsFalseUntilMissingTargetPartitionIsObserved()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var target = await CreateTargetAsync(cancellationToken);
        await using var cluster = target.Cluster;
        using var observer = CreateObserver();
        observer.OnNext(new GrainDirectoryEvents.MembershipVersionApplied(target.SiloAddress, target.Version));

        var observedPartitionCount = 0;
        for (var partitionIndex = 0; partitionIndex < target.PartitionCount - 1; partitionIndex++)
        {
            observer.OnNext(new GrainDirectoryEvents.MembershipVersionObserved(
                target.SiloAddress,
                partitionIndex,
                target.Version));
            observedPartitionCount++;
        }

        Assert.Equal(target.PartitionCount - 1, observedPartitionCount);
        Assert.False(await observer.WaitForConvergenceAsync(cluster.Silos, TimeSpan.Zero));

        var waiter = observer.WaitForConvergenceAsync(cluster.Silos, ConvergenceTimeout);
        Assert.False(
            waiter.IsCompleted,
            $"Partition observation {target.PartitionCount - 1} for {target.SiloAddress} should still be missing.");

        observer.OnNext(new GrainDirectoryEvents.MembershipVersionObserved(
            target.SiloAddress,
            target.PartitionCount - 1,
            target.Version));

        Assert.True(await AwaitConvergenceAsync(waiter, target, "the missing target partition observation", cancellationToken));
    }

    [Fact]
    public async Task WaitForConvergenceAsync_StaleVersionsAndNonTargetAddressEventsDoNotConvergeTarget()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var target = await CreateTargetAsync(cancellationToken);
        await using var cluster = target.Cluster;
        using var observer = CreateObserver();
        var staleVersion = new MembershipVersion(target.Version.Value - 1);
        var newerVersion = new MembershipVersion(target.Version.Value + 1);
        var nonTargetAddress = CreateNonTargetAddress();
        Assert.NotEqual(target.SiloAddress, nonTargetAddress);

        observer.OnNext(new GrainDirectoryEvents.MembershipVersionApplied(target.SiloAddress, staleVersion));
        for (var partitionIndex = 0; partitionIndex < target.PartitionCount; partitionIndex++)
        {
            observer.OnNext(new GrainDirectoryEvents.MembershipVersionObserved(
                target.SiloAddress,
                partitionIndex,
                staleVersion));
        }

        observer.OnNext(new GrainDirectoryEvents.MembershipVersionApplied(nonTargetAddress, newerVersion));
        for (var partitionIndex = 0; partitionIndex < target.PartitionCount; partitionIndex++)
        {
            observer.OnNext(new GrainDirectoryEvents.MembershipVersionObserved(
                nonTargetAddress,
                partitionIndex,
                newerVersion));
        }

        Assert.False(await observer.WaitForConvergenceAsync(cluster.Silos, TimeSpan.Zero));

        var waiter = observer.WaitForConvergenceAsync(cluster.Silos, ConvergenceTimeout);
        Assert.False(
            waiter.IsCompleted,
            $"Stale target observations and complete non-target observations must not converge {target.SiloAddress}.");

        observer.OnNext(new GrainDirectoryEvents.MembershipVersionApplied(target.SiloAddress, target.Version));

        Assert.False(await observer.WaitForConvergenceAsync(cluster.Silos, TimeSpan.Zero));
        Assert.False(
            waiter.IsCompleted,
            $"Stale partition observations should not converge {target.SiloAddress} after the local version is current.");

        var observedPartitionCount = ObserveAllTargetPartitions(observer, target, newerVersion);

        Assert.Equal(target.PartitionCount, observedPartitionCount);
        Assert.True(await AwaitConvergenceAsync(waiter, target, "exact or newer target observations", cancellationToken));
    }

    [Fact]
    public async Task WaitForConvergenceAsync_NewerObservationsAreNotRegressedByStaleEvents()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var target = await CreateTargetAsync(cancellationToken);
        await using var cluster = target.Cluster;
        using var observer = CreateObserver();
        var staleVersion = new MembershipVersion(target.Version.Value - 1);
        var newerVersion = new MembershipVersion(target.Version.Value + 1);
        var waiter = observer.WaitForConvergenceAsync(cluster.Silos, ConvergenceTimeout);
        Assert.False(
            waiter.IsCompleted,
            $"Convergence waiter for {target.SiloAddress} should be armed before monotonic version observations.");

        observer.OnNext(new GrainDirectoryEvents.MembershipVersionApplied(target.SiloAddress, newerVersion));
        observer.OnNext(new GrainDirectoryEvents.MembershipVersionObserved(target.SiloAddress, 0, newerVersion));
        observer.OnNext(new GrainDirectoryEvents.MembershipVersionApplied(target.SiloAddress, staleVersion));
        observer.OnNext(new GrainDirectoryEvents.MembershipVersionObserved(target.SiloAddress, 0, staleVersion));

        Assert.False(
            waiter.IsCompleted,
            $"Partitions 1 through {target.PartitionCount - 1} for {target.SiloAddress} should still be unobserved.");

        for (var partitionIndex = 1; partitionIndex < target.PartitionCount; partitionIndex++)
        {
            observer.OnNext(new GrainDirectoryEvents.MembershipVersionObserved(
                target.SiloAddress,
                partitionIndex,
                target.Version));
        }

        Assert.True(await AwaitConvergenceAsync(waiter, target, "monotonic local and partition observations", cancellationToken));
    }

    [Fact]
    public async Task WaitForConvergenceAsync_PendingTargetOperationBlocksButNonTargetOperationDoesNot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var target = await CreateTargetAsync(cancellationToken);
        await using var cluster = target.Cluster;
        var nonTargetAddress = CreateNonTargetAddress();
        Assert.NotEqual(target.SiloAddress, nonTargetAddress);

        using (var nonTargetObserver = CreateObserver())
        {
            var nonTargetWaiter = nonTargetObserver.WaitForConvergenceAsync(cluster.Silos, ConvergenceTimeout);
            Assert.False(
                nonTargetWaiter.IsCompleted,
                $"Convergence waiter for {target.SiloAddress} should be armed before the non-target operation.");

            nonTargetObserver.OnNext(CreateRangeOperationStarted(nonTargetAddress, target.Version));
            var observedPartitionCount = ObserveAllTargetVersions(nonTargetObserver, target);

            Assert.Equal(target.PartitionCount, observedPartitionCount);
            Assert.True(await AwaitConvergenceAsync(
                nonTargetWaiter,
                target,
                $"target observations with a pending non-target operation for {nonTargetAddress}",
                cancellationToken));
        }

        using var targetObserver = CreateObserver();
        ObserveAllTargetVersions(targetObserver, target);
        var targetOperation = CreateRangeOperationStarted(target.SiloAddress, target.Version);
        targetObserver.OnNext(targetOperation);

        Assert.False(await targetObserver.WaitForConvergenceAsync(cluster.Silos, TimeSpan.Zero));

        var targetWaiter = targetObserver.WaitForConvergenceAsync(cluster.Silos, ConvergenceTimeout);
        Assert.False(
            targetWaiter.IsCompleted,
            $"Pending target operation for {target.SiloAddress} should block convergence.");

        targetObserver.OnNext(CreateRangeOperationCompleted(targetOperation));

        Assert.True(await AwaitConvergenceAsync(
            targetWaiter,
            target,
            "the exact pending target operation completion",
            cancellationToken));
    }

    [Fact]
    public async Task OnError_ReleasesArmedWaiterWithExactMessageAndSameInnerException()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var target = await CreateTargetAsync(cancellationToken);
        await using var cluster = target.Cluster;
        var expectedError = new InvalidOperationException("Controlled observer failure.");
        var eventSource = new TestEventSource();
        using (var observer = new GrainDirectoryObserver(eventSource))
        {
            Assert.True(eventSource.HasObserver);
            var waiter = observer.WaitForConvergenceAsync(cluster.Silos, ConvergenceTimeout);
            Assert.False(
                waiter.IsCompleted,
                $"Convergence waiter for {target.SiloAddress} should be armed before the observer error.");

            observer.OnError(expectedError);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => { await waiter; });
            Assert.Equal("An error occurred while observing grain directory events.", exception.Message);
            Assert.Same(expectedError, exception.InnerException);
        }

        Assert.False(eventSource.HasObserver);
    }

    private static async Task<TargetFixture> CreateTargetAsync(CancellationToken cancellationToken)
    {
        var targets = await CreateTargetsAsync(siloCount: 1, cancellationToken);
        return Assert.Single(targets);
    }

    private static async Task<TargetFixture[]> CreateTargetsAsync(short siloCount, CancellationToken cancellationToken)
    {
        var builder = new InProcessTestClusterBuilder(siloCount);
        builder.Options.ConfigureFileLogging = false;
        builder.Options.InitializeClientOnDeploy = false;
#pragma warning disable ORLEANSEXP003
        builder.Options.UseDistributedGrainDirectory = true;
        builder.ConfigureSilo(static (_, siloBuilder) =>
            siloBuilder.Configure<GrainDirectoryOptions>(options => options.PartitionsPerSilo = PartitionCount));
#pragma warning restore ORLEANSEXP003

        var cluster = builder.Build();
        try
        {
            await cluster.DeployAsync(cancellationToken);
            Assert.Equal((int)siloCount, cluster.Silos.Count);
            return cluster.Silos.Select(silo =>
            {
                var membershipVersion = silo.ServiceProvider
                    .GetRequiredService<IClusterMembershipService>()
                    .CurrentSnapshot.Version;
                var partitionCount = silo.ServiceProvider
                    .GetRequiredService<IOptions<GrainDirectoryOptions>>()
                    .Value.PartitionsPerSilo;

                Assert.Equal(PartitionCount, partitionCount);
                return new TargetFixture(cluster, silo.SiloAddress, membershipVersion, partitionCount);
            }).ToArray();
        }
        catch
        {
            await cluster.DisposeAsync();
            throw;
        }
    }

    private static GrainDirectoryObserver CreateObserver() => new(new TestEventSource());

    private static int ObserveAllTargetVersions(GrainDirectoryObserver observer, TargetFixture target)
    {
        observer.OnNext(new GrainDirectoryEvents.MembershipVersionApplied(target.SiloAddress, target.Version));
        return ObserveAllTargetPartitions(observer, target, target.Version);
    }

    private static int ObserveAllTargetPartitions(
        GrainDirectoryObserver observer,
        TargetFixture target,
        MembershipVersion version)
    {
        var observedPartitionCount = 0;
        for (var partitionIndex = 0; partitionIndex < target.PartitionCount; partitionIndex++)
        {
            observer.OnNext(new GrainDirectoryEvents.MembershipVersionObserved(
                target.SiloAddress,
                partitionIndex,
                version));
            observedPartitionCount++;
        }

        return observedPartitionCount;
    }

    private static GrainDirectoryEvents.RangeOperationStarted CreateRangeOperationStarted(
        SiloAddress siloAddress,
        MembershipVersion version) =>
        new(
            siloAddress,
            partitionIndex: 1,
            version,
            OperationRange,
            GrainDirectoryEvents.AcquireOperationName);

    private static GrainDirectoryEvents.RangeOperationCompleted CreateRangeOperationCompleted(
        GrainDirectoryEvents.RangeOperationStarted started,
        int? partitionIndex = null,
        MembershipVersion? version = null,
        RingRange? range = null,
        string? operationName = null) =>
        new(
            started.SiloAddress,
            partitionIndex ?? started.PartitionIndex,
            version ?? started.Version,
            range ?? started.Range,
            operationName ?? started.OperationName,
            heldDuration: TimeSpan.Zero,
            canceled: false);

    private static SiloAddress CreateNonTargetAddress() =>
        SiloAddress.New(IPAddress.Loopback, port: 1, generation: 1);

    private static async Task<bool> AwaitConvergenceAsync(
        Task<bool> waiter,
        TargetFixture target,
        string phase,
        CancellationToken cancellationToken)
    {
        try
        {
            return await waiter.WaitAsync(ConvergenceTimeout, cancellationToken);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                $"Timed out waiting for {phase}: target={target.SiloAddress}, "
                + $"version={target.Version}, partitions={target.PartitionCount}, waiterCompleted={waiter.IsCompleted}.",
                exception);
        }
    }

    private sealed record TargetFixture(
        InProcessTestCluster Cluster,
        SiloAddress SiloAddress,
        MembershipVersion Version,
        int PartitionCount);

    private sealed class TestEventSource : IObservable<GrainDirectoryEvents.GrainDirectoryEvent>
    {
        public bool HasObserver { get; private set; }

        public IDisposable Subscribe(IObserver<GrainDirectoryEvents.GrainDirectoryEvent> observer)
        {
            Assert.False(HasObserver);
            HasObserver = true;
            return new Subscription(this);
        }

        private sealed class Subscription(TestEventSource source) : IDisposable
        {
            public void Dispose() => source.HasObserver = false;
        }
    }
}
