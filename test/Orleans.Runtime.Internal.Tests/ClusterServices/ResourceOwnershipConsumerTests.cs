using Orleans.Runtime;
using Orleans.Runtime.ClusterServices;
using Orleans.Runtime.GrainDirectory;
using TestExtensions;
using Xunit;
using static UnitTests.ClusterServices.RegisteredClusterServiceViewProviderTests;

namespace UnitTests.ClusterServices;

[TestArea("Runtime"), TestCategory("BVT"), TestSuite("BVT"), TestProvider("None")]
public sealed class ResourceOwnershipConsumerTests
{
    [Fact(Timeout = 30_000)]
    public async Task FixedMembershipReassignmentDrainsPredecessorInstallsStateAndFencesBeforeServing()
    {
        var membership = new TestServiceMembership();
        await using var provider = Create(new(), membership);
        var protocol = new Protocol();
        await using var source = new ResourceOwnershipConsumer(TestServiceMembership.A, provider, protocol);
        await using var destination = new ResourceOwnershipConsumer(TestServiceMembership.B, provider, protocol);
        protocol.Consumers[TestServiceMembership.A] = source;
        protocol.Consumers[TestServiceMembership.B] = destination;
        var first = await Publish(provider, TestServiceMembership.A);
        await Task.WhenAll(source.InstallViewAsync(first), destination.InstallViewAsync(first)).WaitAsync(TestContext.Current.CancellationToken);
        await source.ExecuteAsync(TestServiceMembership.Resource, first.Id,
            static (_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 42 }), TestContext.Current.CancellationToken);
        var operationStarted = Signal();
        var completeOperation = Signal();
        var inFlight = source.ExecuteAsync(TestServiceMembership.Resource, first.Id, async (_, token) =>
        {
            operationStarted.SetResult();
            await completeOperation.Task.WaitAsync(token);
            return new byte[] { 99 };
        }, TestContext.Current.CancellationToken).AsTask();
        await operationStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var fenceRequested = Signal();
        var establishFence = new TaskCompletionSource<ClusterServiceFence>(TaskCreationOptions.RunContinuationsAsynchronously);
        protocol.Fence = (_, _, token) =>
        {
            fenceRequested.TrySetResult();
            return new(establishFence.Task.WaitAsync(token));
        };

        var second = await Publish(provider, TestServiceMembership.B);
        var release = source.InstallViewAsync(second);
        var acquire = destination.InstallViewAsync(second);
        var serving = Read(destination, second.Id).AsTask();
        Assert.False(release.IsCompleted);
        Assert.False(acquire.IsCompleted);
        Assert.False(serving.IsCompleted);
        Assert.Equal(0, protocol.Checkpoints);
        completeOperation.SetResult();
        await Assert.ThrowsAsync<ClusterServiceViewUnavailableException>(() => inFlight);
        await release.WaitAsync(TestContext.Current.CancellationToken);
        await fenceRequested.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(acquire.IsCompleted);
        Assert.False(serving.IsCompleted);
        Assert.Equal(1, protocol.Checkpoints);

        establishFence.SetResult(new(ClusterServiceFencingMode.External, 987654));
        await acquire.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 42 }, (await serving).ToArray());
        Assert.Equal(1, protocol.Recoveries);
        Assert.Equal(1, protocol.Handoffs);
        Assert.Same(membership.InitialSnapshot, membership.CurrentSnapshot);
        await Assert.ThrowsAsync<ClusterServiceViewUnavailableException>(() => Read(source, first.Id).AsTask());
    }

    [Fact(Timeout = 30_000)]
    public async Task SkippedAToBToASelectsRecoveryEvenWhenTheLocalOwnedSetIsIdentical()
    {
        await using var provider = Create(new(), new());
        var protocol = new Protocol();
        await using var consumer = new ResourceOwnershipConsumer(TestServiceMembership.A, provider, protocol);
        var first = await Publish(provider, TestServiceMembership.A);
        await consumer.InstallViewAsync(first).WaitAsync(TestContext.Current.CancellationToken);
        await consumer.ExecuteAsync(TestServiceMembership.Resource, first.Id,
            static (_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 17 }), TestContext.Current.CancellationToken);
        var skipped = await Publish(provider, TestServiceMembership.B);
        var latest = await Publish(provider, TestServiceMembership.A);

        await consumer.InstallViewAsync(latest).WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(skipped.Id, latest.Predecessor);
        Assert.Equal(first.GetOwnedResources(TestServiceMembership.A), latest.GetOwnedResources(TestServiceMembership.A));
        Assert.Equal(2, protocol.Recoveries);
        Assert.Equal(0, protocol.Handoffs);
        Assert.Equal(1, protocol.Checkpoints);
        Assert.Equal(new byte[] { 17 }, (await Read(consumer, latest.Id)).ToArray());
        await Assert.ThrowsAsync<ClusterServiceViewUnavailableException>(() => Read(consumer, first.Id).AsTask());
    }

    [Fact(Timeout = 30_000)]
    public async Task LocalOwnedSetDeltasPreserveRetainedResourcesAndHandleBidirectionalTransfers()
    {
        await using var provider = Create(new(), new());
        var protocol = new Protocol
        {
            Recover = (resource, _, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { byte.Parse(resource) })
        };
        await using var firstOwner = new ResourceOwnershipConsumer(TestServiceMembership.A, provider, protocol);
        await using var secondOwner = new ResourceOwnershipConsumer(TestServiceMembership.B, provider, protocol);
        protocol.Consumers[TestServiceMembership.A] = firstOwner;
        protocol.Consumers[TestServiceMembership.B] = secondOwner;
        var initialMapping = new Dictionary<string, SiloAddress>
        {
            ["10"] = TestServiceMembership.A,
            ["20"] = TestServiceMembership.A,
            ["30"] = TestServiceMembership.B
        };
        var initial = (await provider.TryPublishAsync(Configuration, [TestServiceMembership.B, TestServiceMembership.A],
            initialMapping.Keys, initialMapping, TestContext.Current.CancellationToken))!;
        await Task.WhenAll(firstOwner.InstallViewAsync(initial), secondOwner.InstallViewAsync(initial)).WaitAsync(TestContext.Current.CancellationToken);
        var mapping = new Dictionary<string, SiloAddress>
        {
            ["10"] = TestServiceMembership.A,
            ["20"] = TestServiceMembership.B,
            ["30"] = TestServiceMembership.A
        };
        var next = (await provider.TryPublishAsync(Configuration, [TestServiceMembership.A, TestServiceMembership.B],
            mapping.Keys, mapping, TestContext.Current.CancellationToken))!;

        await Task.WhenAll(firstOwner.InstallViewAsync(next), secondOwner.InstallViewAsync(next)).WaitAsync(TestContext.Current.CancellationToken);

        foreach (var (resource, owner) in mapping)
        {
            Assert.Equal(owner, next.ResourceOwners[resource]);
            var state = await protocol.Consumers[owner].ExecuteAsync(resource, next.Id,
                static (value, _) => ValueTask.FromResult(value), TestContext.Current.CancellationToken);
            Assert.Equal(new byte[] { byte.Parse(resource) }, state.ToArray());
        }

        Assert.Equal(new[] { "10", "30" }, next.GetOwnedResources(TestServiceMembership.A).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "20" }, next.GetOwnedResources(TestServiceMembership.B));
        Assert.Equal(3, protocol.Recoveries);
        Assert.Equal(2, protocol.Handoffs);
        Assert.Equal(2, protocol.Checkpoints);
        Assert.Equal(5, protocol.Fences);
    }

    [Fact(Timeout = 30_000)]
    public async Task RegisteredRingReassignmentUsesStableResourceIdsThroughProductionTransitions()
    {
        await using var provider = Create(new(), new());
        var protocol = new Protocol
        {
            Recover = (resource, _, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { byte.Parse(resource) })
        };
        await using var firstOwner = new ResourceOwnershipConsumer(TestServiceMembership.A, provider, protocol);
        await using var secondOwner = new ResourceOwnershipConsumer(TestServiceMembership.B, provider, protocol);
        protocol.Consumers[TestServiceMembership.A] = firstOwner;
        protocol.Consumers[TestServiceMembership.B] = secondOwner;
        var ranges = new Dictionary<string, RingRange>
        {
            ["10"] = RingRange.Create(0, 100),
            ["20"] = RingRange.Create(100, 0)
        };
        var first = (await provider.TryPublishAsync(Configuration, [TestServiceMembership.A, TestServiceMembership.B], ranges.Keys,
            [KeyValuePair.Create("10", TestServiceMembership.A), KeyValuePair.Create("20", TestServiceMembership.B)],
            TestContext.Current.CancellationToken,
            [new(TestServiceMembership.A, 0, ranges["10"]), new(TestServiceMembership.B, 0, ranges["20"])], ranges))!;
        await Task.WhenAll(firstOwner.InstallViewAsync(first), secondOwner.InstallViewAsync(first)).WaitAsync(TestContext.Current.CancellationToken);
        var next = (await provider.TryPublishAsync(Configuration, [TestServiceMembership.A, TestServiceMembership.B], ranges.Keys,
            [KeyValuePair.Create("10", TestServiceMembership.B), KeyValuePair.Create("20", TestServiceMembership.A)],
            TestContext.Current.CancellationToken,
            [new(TestServiceMembership.B, 0, ranges["10"]), new(TestServiceMembership.A, 0, ranges["20"])], ranges))!;

        await Task.WhenAll(firstOwner.InstallViewAsync(next), secondOwner.InstallViewAsync(next)).WaitAsync(TestContext.Current.CancellationToken);

        foreach (var point in new uint[] { 0, 1, 100, 101, uint.MaxValue })
        {
            var expectedResource = Assert.Single(ranges, entry => entry.Value.Contains(point)).Key;
            Assert.True(first.TryGetRingResource(point, out var previousResource, out var previousOwner));
            Assert.True(next.TryGetRingResource(point, out var resource, out var owner));
            Assert.NotNull(resource);
            Assert.NotNull(owner);
            Assert.Equal(expectedResource, resource);
            Assert.Equal(previousResource, resource);
            Assert.NotEqual(previousOwner, owner);
            var state = await protocol.Consumers[owner].ExecuteRingAsync(point, next.Id,
                static (value, _) => ValueTask.FromResult(value), TestContext.Current.CancellationToken);
            Assert.Equal(new byte[] { byte.Parse(expectedResource) }, state.ToArray());
        }

        Assert.Equal(2, protocol.Recoveries);
        Assert.Equal(2, protocol.Handoffs);
        Assert.Equal(2, protocol.Checkpoints);
        Assert.Equal(4, protocol.Fences);
    }

    [Fact(Timeout = 30_000)]
    public async Task ConcurrentTransformsExecuteSequentiallyWithoutLosingUpdates()
    {
        await using var provider = Create(new(), new());
        var protocol = new Protocol();
        await using var consumer = new ResourceOwnershipConsumer(TestServiceMembership.A, provider, protocol);
        var view = await Publish(provider, TestServiceMembership.A);
        await consumer.InstallViewAsync(view).WaitAsync(TestContext.Current.CancellationToken);
        var firstStarted = Signal();
        var finishFirst = Signal();
        var first = consumer.ExecuteAsync(TestServiceMembership.Resource, view.Id, async (state, token) =>
        {
            var observed = state.Span[0];
            firstStarted.SetResult();
            await finishFirst.Task.WaitAsync(token);
            return new byte[] { (byte)(observed + 10) };
        }, TestContext.Current.CancellationToken).AsTask();
        await firstStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        var secondStarted = false;
        var second = consumer.ExecuteAsync(TestServiceMembership.Resource, view.Id, (state, _) =>
        {
            secondStarted = true;
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { (byte)(state.Span[0] + 1) });
        }, TestContext.Current.CancellationToken).AsTask();
        Assert.False(secondStarted);
        Assert.False(second.IsCompleted);
        finishFirst.SetResult();

        Assert.Equal(new byte[] { 11 }, (await first.WaitAsync(TestContext.Current.CancellationToken)).ToArray());
        Assert.Equal(new byte[] { 12 }, (await second.WaitAsync(TestContext.Current.CancellationToken)).ToArray());
        Assert.True(secondStarted);
        Assert.Equal(new byte[] { 12 }, (await Read(consumer, view.Id)).ToArray());
        var next = await consumer.ExecuteAsync(TestServiceMembership.Resource, view.Id,
            static (state, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { (byte)(state.Span[0] + 1) }),
            TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 13 }, next.ToArray());
        Assert.Equal(1, protocol.Fences);
    }

    [Fact(Timeout = 30_000)]
    public async Task QueuedTransformCancellationDoesNotCancelTheExecutingReceiver()
    {
        await using var provider = Create(new(), new());
        await using var consumer = new ResourceOwnershipConsumer(TestServiceMembership.A, provider, new Protocol());
        var view = await Publish(provider, TestServiceMembership.A);
        await consumer.InstallViewAsync(view).WaitAsync(TestContext.Current.CancellationToken);
        var started = Signal();
        var release = Signal();
        var executing = consumer.ExecuteAsync(TestServiceMembership.Resource, view.Id, async (_, token) =>
        {
            started.SetResult();
            await release.Task.WaitAsync(token);
            return new byte[] { 9 };
        }, TestContext.Current.CancellationToken).AsTask();
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var queuedStarted = false;
        var queued = consumer.ExecuteAsync(TestServiceMembership.Resource, view.Id, (state, _) =>
        {
            queuedStarted = true;
            return ValueTask.FromResult(state);
        }, cancellation.Token).AsTask();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.False(queuedStarted);
        Assert.False(executing.IsCompleted);
        release.SetResult();
        Assert.Equal(new byte[] { 9 }, (await executing.WaitAsync(TestContext.Current.CancellationToken)).ToArray());
        Assert.Equal(new byte[] { 9 }, (await Read(consumer, view.Id)).ToArray());
    }

    [Fact(Timeout = 30_000)]
    public async Task DisposalCancelsQueuedAndActiveOperationsAndWaitsForReceiverCleanup()
    {
        await using var provider = Create(new(), new());
        var protocol = new Protocol();
        await using var consumer = new ResourceOwnershipConsumer(TestServiceMembership.A, provider, protocol);
        var view = await Publish(provider, TestServiceMembership.A);
        await consumer.InstallViewAsync(view).WaitAsync(TestContext.Current.CancellationToken);
        var started = Signal();
        var cancellationObserved = Signal();
        var finishCleanup = Signal();
        var waitForShutdown = Signal();
        var exited = false;
        CancellationToken operationToken = default;
        using var caller = new CancellationTokenSource();
        var active = consumer.ExecuteAsync(TestServiceMembership.Resource, view.Id, async (_, token) =>
        {
            operationToken = token;
            started.SetResult();
            try
            {
                await waitForShutdown.Task.WaitAsync(token);
                return new byte[] { 255 };
            }
            finally
            {
                cancellationObserved.TrySetResult();
                await finishCleanup.Task.WaitAsync(TestContext.Current.CancellationToken);
                exited = true;
            }
        }, caller.Token).AsTask();
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        var queuedStarted = false;
        var queued = consumer.ExecuteAsync(TestServiceMembership.Resource, view.Id, (state, _) =>
        {
            queuedStarted = true;
            return ValueTask.FromResult(state);
        }, caller.Token).AsTask();

        var disposal = consumer.DisposeAsync().AsTask();
        await cancellationObserved.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(disposal.IsCompleted);
        Assert.False(exited);
        Assert.False(caller.IsCancellationRequested);
        Assert.True(operationToken.IsCancellationRequested);
        finishCleanup.SetResult();
        await disposal.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(exited);
        Assert.False(queuedStarted);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.Equal(new byte[] { 1 }, protocol.Durable.ToArray());
        Assert.Equal(0, protocol.Checkpoints);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Read(consumer, view.Id).AsTask());
    }

    [Fact(Timeout = 30_000)]
    public async Task CallerCancellationNeverCancelsSharedAcquisition()
    {
        await using var provider = Create(new(), new());
        var recovery = new TaskCompletionSource<ReadOnlyMemory<byte>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken sharedToken = default;
        var protocol = new Protocol
        {
            Recover = (_, _, token) =>
            {
                sharedToken = token;
                return new(recovery.Task.WaitAsync(token));
            }
        };
        await using var consumer = new ResourceOwnershipConsumer(TestServiceMembership.A, provider, protocol);
        var view = await Publish(provider, TestServiceMembership.A);
        var installation = consumer.InstallViewAsync(view);
        Assert.Same(installation, consumer.InstallViewAsync(view));
        using var cancellation = new CancellationTokenSource();
        var cancelled = consumer.ExecuteAsync(TestServiceMembership.Resource, view.Id,
            static (state, _) => ValueTask.FromResult(state), cancellation.Token).AsTask();
        var unaffected = Read(consumer, view.Id).AsTask();

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.False(sharedToken.IsCancellationRequested);
        Assert.False(installation.IsCompleted);
        Assert.False(unaffected.IsCompleted);
        recovery.SetResult(new byte[] { 73 });

        await installation.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 73 }, (await unaffected).ToArray());
        Assert.Equal(1, protocol.Recoveries);
    }

    [Fact(Timeout = 30_000)]
    public async Task FailedExternalFenceRetainsTheOriginalExceptionAndAdmissionFailsClosed()
    {
        await using var provider = Create(new(), new());
        var failure = new IOException("External receiver fencing failed.");
        var protocol = new Protocol { Fence = (_, _, _) => ValueTask.FromException<ClusterServiceFence>(failure) };
        await using var consumer = new ResourceOwnershipConsumer(TestServiceMembership.A, provider, protocol);
        var view = await Publish(provider, TestServiceMembership.A);

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => consumer.InstallViewAsync(view).WaitAsync(TestContext.Current.CancellationToken)));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => consumer.InstallViewAsync(view).WaitAsync(TestContext.Current.CancellationToken)));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => Read(consumer, view.Id).AsTask()));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => Read(consumer, view.Id).AsTask()));
        Assert.Equal(1, protocol.Recoveries);
    }

    [Fact(Timeout = 30_000)]
    public async Task PlacementRevisionCannotSubstituteForTheExternalFence()
    {
        await using var provider = Create(new(), new());
        var protocol = new Protocol { Fence = (_, view, _) => ValueTask.FromResult(new ClusterServiceFence(ClusterServiceFencingMode.MembershipView, view.Revision)) };
        await using var consumer = new ResourceOwnershipConsumer(TestServiceMembership.A, provider, protocol);
        var view = await Publish(provider, TestServiceMembership.A);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => consumer.InstallViewAsync(view).WaitAsync(TestContext.Current.CancellationToken));
        Assert.Contains("external provider fence", failure.Message);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => Read(consumer, view.Id).AsTask()));
    }

    [Fact(Timeout = 30_000)]
    public async Task SnapshotForADifferentReceiverFailsClosedWithoutInstallingItsState()
    {
        await using var provider = Create(new(), new());
        var protocol = new Protocol { AfterSnapshot = response => Task.FromResult(response with { ReceiverId = Guid.NewGuid(), State = new byte[] { 255 } }) };
        await using var source = new ResourceOwnershipConsumer(TestServiceMembership.A, provider, protocol);
        await using var destination = new ResourceOwnershipConsumer(TestServiceMembership.B, provider, protocol);
        protocol.Consumers[TestServiceMembership.A] = source;
        var first = await Publish(provider, TestServiceMembership.A);
        await Task.WhenAll(source.InstallViewAsync(first), destination.InstallViewAsync(first)).WaitAsync(TestContext.Current.CancellationToken);
        var next = await Publish(provider, TestServiceMembership.B);
        await source.InstallViewAsync(next).WaitAsync(TestContext.Current.CancellationToken);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => destination.InstallViewAsync(next).WaitAsync(TestContext.Current.CancellationToken));

        Assert.Contains("does not match receiver", failure.Message);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => Read(destination, next.Id).AsTask()));
        Assert.Equal(1, protocol.Fences);
    }

    [Fact(Timeout = 30_000)]
    public async Task DelayedSnapshotReplyCannotInstallIntoANewerReceiver()
    {
        await using var provider = Create(new(), new());
        var protocol = new Protocol();
        await using var source = new ResourceOwnershipConsumer(TestServiceMembership.A, provider, protocol);
        await using var destination = new ResourceOwnershipConsumer(TestServiceMembership.B, provider, protocol);
        protocol.Consumers[TestServiceMembership.A] = source;
        protocol.Consumers[TestServiceMembership.B] = destination;
        var first = await Publish(provider, TestServiceMembership.A);
        await Task.WhenAll(source.InstallViewAsync(first), destination.InstallViewAsync(first)).WaitAsync(TestContext.Current.CancellationToken);
        var replyCaptured = Signal();
        var deliverReply = Signal();
        protocol.AfterSnapshot = async response =>
        {
            replyCaptured.SetResult();
            await deliverReply.Task.WaitAsync(TestContext.Current.CancellationToken);
            return response with { State = new byte[] { 255 } };
        };
        var second = await Publish(provider, TestServiceMembership.B);
        await source.InstallViewAsync(second).WaitAsync(TestContext.Current.CancellationToken);
        var staleAcquisition = destination.InstallViewAsync(second);
        await replyCaptured.Task.WaitAsync(TestContext.Current.CancellationToken);
        var latest = await Publish(provider, TestServiceMembership.B);
        protocol.Durable = new byte[] { 63 };
        var currentAcquisition = destination.InstallViewAsync(latest);
        var currentRead = Read(destination, latest.Id).AsTask();
        Assert.False(currentRead.IsCompleted);

        deliverReply.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => staleAcquisition.WaitAsync(TestContext.Current.CancellationToken));
        await currentAcquisition.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new byte[] { 63 }, (await currentRead).ToArray());
        Assert.Equal(2, protocol.Recoveries);
        Assert.Equal(1, protocol.Handoffs);
        await Assert.ThrowsAsync<ClusterServiceViewUnavailableException>(() => Read(destination, second.Id).AsTask());
        await Assert.ThrowsAsync<ClusterServiceViewUnavailableException>(() => source.CreateHandoffAsync(
            new(TestServiceMembership.Resource, TestServiceMembership.A, first.Id, second.Id, Guid.NewGuid()),
            TestContext.Current.CancellationToken).AsTask());
    }

    [Fact(Timeout = 30_000)]
    public async Task DeadOwnerAndUnavailableAuthorityDoNotSilentlyTransferOrServe()
    {
        var register = new TestServiceViewRegister();
        var membership = new TestServiceMembership();
        await using var provider = Create(register, membership);
        var protocol = new Protocol();
        await using var source = new ResourceOwnershipConsumer(TestServiceMembership.A, provider, protocol);
        await using var destination = new ResourceOwnershipConsumer(TestServiceMembership.B, provider, protocol);
        var first = await Publish(provider, TestServiceMembership.A);
        await Task.WhenAll(source.InstallViewAsync(first), destination.InstallViewAsync(first)).WaitAsync(TestContext.Current.CancellationToken);
        membership.SetStatus(TestServiceMembership.A, SiloStatus.Dead);
        await provider.RefreshLivenessAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ClusterServiceViewUnavailableException>(() => Read(source, first.Id).AsTask());
        await Assert.ThrowsAsync<ClusterServiceViewUnavailableException>(() => Read(destination, first.Id).AsTask());
        Assert.Equal(1, register.SuccessfulWrites);
        var next = await Publish(provider, TestServiceMembership.B, [TestServiceMembership.B]);
        await destination.InstallViewAsync(next).WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, protocol.Recoveries);
        Assert.Equal(0, protocol.Handoffs);
        Assert.Equal(new byte[] { 1 }, (await Read(destination, next.Id)).ToArray());

        register.Failure = new IOException("Authority offline.");
        await Assert.ThrowsAsync<IOException>(() => provider.RefreshAsync(TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<ClusterServiceViewUnavailableException>(() => Read(destination, next.Id).AsTask());
    }

    [Fact(Timeout = 30_000)]
    public async Task ShutdownCancelsPendingSharedTransitionAndBlockedAdmission()
    {
        await using var provider = Create(new(), new());
        var recovery = new TaskCompletionSource<ReadOnlyMemory<byte>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var protocol = new Protocol { Recover = (_, _, token) => new(recovery.Task.WaitAsync(token)) };
        var consumer = new ResourceOwnershipConsumer(TestServiceMembership.A, provider, protocol);
        var view = await Publish(provider, TestServiceMembership.A);
        var installation = consumer.InstallViewAsync(view);
        var pending = Read(consumer, view.Id).AsTask();

        await consumer.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installation.WaitAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Read(consumer, view.Id).AsTask());
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static ValueTask<ReadOnlyMemory<byte>> Read(ResourceOwnershipConsumer consumer, RegisteredServiceViewId view) =>
        consumer.ExecuteAsync(TestServiceMembership.Resource, view, static (state, _) => ValueTask.FromResult(state), TestContext.Current.CancellationToken);

    private sealed class Protocol : IResourceOwnershipProtocol
    {
        public Dictionary<SiloAddress, ResourceOwnershipConsumer> Consumers { get; } = [];
        public ReadOnlyMemory<byte> Durable { get; set; } = new byte[] { 1 };
        public Func<string, RegisteredServiceViewId, CancellationToken, ValueTask<ReadOnlyMemory<byte>>>? Recover { get; init; }
        public Func<string, RegisteredServiceViewId, CancellationToken, ValueTask<ClusterServiceFence>>? Fence { get; set; }
        public Func<ResourceHandoffState, Task<ResourceHandoffState>>? AfterSnapshot { get; set; }
        public int Recoveries { get; private set; }
        public int Handoffs { get; private set; }
        public int Checkpoints { get; private set; }
        public int Fences { get; private set; }

        public ValueTask<ReadOnlyMemory<byte>> RecoverAsync(string resource, RegisteredServiceViewId targetView, CancellationToken cancellationToken)
        {
            Recoveries++;
            return Recover?.Invoke(resource, targetView, cancellationToken) ?? ValueTask.FromResult(Durable);
        }

        public async ValueTask<ResourceHandoffState> RequestHandoffAsync(SiloAddress source, ResourceHandoffRequest request, CancellationToken cancellationToken)
        {
            Handoffs++;
            var response = await Consumers[source].CreateHandoffAsync(request, cancellationToken);
            return AfterSnapshot is { } delay ? await delay(response).WaitAsync(cancellationToken) : response;
        }

        public ValueTask CheckpointAsync(string resource, ReadOnlyMemory<byte> state, RegisteredServiceViewId previousView, CancellationToken cancellationToken)
        {
            Checkpoints++;
            Durable = state.ToArray();
            return ValueTask.CompletedTask;
        }

        public ValueTask<ClusterServiceFence> AcquireFenceAsync(string resource, RegisteredServiceViewId targetView, CancellationToken cancellationToken)
        {
            Fences++;
            return Fence?.Invoke(resource, targetView, cancellationToken) ?? ValueTask.FromResult(new ClusterServiceFence(ClusterServiceFencingMode.External, 12345));
        }
    }
}
