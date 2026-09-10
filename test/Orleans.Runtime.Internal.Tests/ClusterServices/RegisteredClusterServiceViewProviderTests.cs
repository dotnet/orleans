using System.Collections.Immutable;
using Orleans.Runtime;
using Orleans.Runtime.ClusterServices;
using Orleans.Runtime.GrainDirectory;
using TestExtensions;
using Xunit;

namespace UnitTests.ClusterServices;

[TestArea("Runtime"), TestCategory("BVT"), TestSuite("BVT"), TestProvider("None")]
public sealed class RegisteredClusterServiceViewProviderTests
{
    [Fact(Timeout = 30_000)]
    public async Task InitialAbsenceIsUnavailableAndLaterPublicationInitializesTheProvider()
    {
        var register = new TestServiceViewRegister();
        var membership = new TestServiceMembership();
        await using var provider = Create(register, membership);
        Assert.False(provider.TryGetCurrentView(out _));
        await Assert.ThrowsAsync<ClusterServiceViewUnavailableException>(() => provider.RefreshAsync(TestContext.Current.CancellationToken).AsTask());

        var view = await Publish(provider, TestServiceMembership.A);

        Assert.NotNull(view);
        Assert.Equal(1, view.Id.Revision);
        Assert.False(view.TryGetPredecessor(out _));
        Assert.Same(view, await provider.RefreshAsync(TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 30_000)]
    public async Task MappingAndParticipationChangeAtomicallyWithFixedMembershipAcrossObservers()
    {
        var register = new TestServiceViewRegister();
        var membership = new TestServiceMembership();
        await using var writer = Create(register, membership);
        await using var reader = Create(register, membership);
        var first = await Publish(writer, TestServiceMembership.A, [TestServiceMembership.B, TestServiceMembership.A]);
        var observed = await reader.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new[] { TestServiceMembership.A, TestServiceMembership.B }, observed.Participants);
        Assert.Same(membership.CurrentSnapshot, membership.InitialSnapshot);

        var second = await Publish(writer, TestServiceMembership.B, [TestServiceMembership.B]);
        observed = await reader.RefreshAtLeastAsync(second.Id, TestContext.Current.CancellationToken);

        Assert.Equal(first.Id, observed.Predecessor);
        Assert.Equal(first.MembershipWatermark, observed.MembershipWatermark);
        Assert.Equal(new[] { TestServiceMembership.B }, observed.Participants);
        Assert.Equal(TestServiceMembership.B, observed.ResourceOwners[TestServiceMembership.Resource]);
        Assert.Empty(observed.GetOwnedResources(TestServiceMembership.A));
        Assert.Equal([TestServiceMembership.Resource], observed.GetOwnedResources(TestServiceMembership.B));
        Assert.True(second.HasSameContent(observed));
        Assert.Same(membership.InitialSnapshot, membership.CurrentSnapshot);
    }

    [Fact(Timeout = 30_000)]
    public async Task ConcurrentWritersHaveOneCasWinnerAndRecordTheActualPredecessor()
    {
        var register = new TestServiceViewRegister();
        var membership = new TestServiceMembership();
        await using var firstWriter = Create(register, membership);
        await using var secondWriter = Create(register, membership);
        var initial = await Publish(firstWriter, TestServiceMembership.A);
        var releaseReads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        register.AfterRead = async () =>
        {
            if (Interlocked.Increment(ref reads) == 2)
            {
                bothRead.SetResult();
            }

            await releaseReads.Task.WaitAsync(TestContext.Current.CancellationToken);
        };
        var first = Publish(firstWriter, TestServiceMembership.B).AsTask();
        var second = Publish(secondWriter, TestServiceMembership.A).AsTask();
        await bothRead.Task.WaitAsync(TestContext.Current.CancellationToken);
        releaseReads.SetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(TestContext.Current.CancellationToken);
        var winner = Assert.Single(results, static result => result is not null)!;

        Assert.Single(results, static result => result is null);
        Assert.Equal(initial.Id, winner.Predecessor);
        Assert.Equal(initial.Id.Revision + 1, winner.Id.Revision);
        Assert.Same(winner, register.Current.View);
        Assert.Equal(2, register.SuccessfulWrites);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicationRetainsItsOwnWriteTokenWhenAnotherWriterAdvancesBeforeTheReply(bool hasPredecessor)
    {
        var register = new TestServiceViewRegister();
        await using var firstWriter = Create(register, new());
        await using var secondWriter = Create(register, new());
        if (hasPredecessor)
        {
            await Publish(firstWriter, TestServiceMembership.A);
        }

        var targetRevision = hasPredecessor ? 2 : 1;
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliverReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        register.AfterWrite = view =>
        {
            if (view.Id.Revision != targetRevision)
            {
                return Task.CompletedTask;
            }

            committed.SetResult();
            return deliverReply.Task;
        };
        var publication = Publish(firstWriter, TestServiceMembership.A).AsTask();
        await committed.Task.WaitAsync(TestContext.Current.CancellationToken);
        var advanced = await Publish(secondWriter, TestServiceMembership.B);
        deliverReply.SetResult();
        var published = await publication.WaitAsync(TestContext.Current.CancellationToken);
        register.AfterWrite = null;
        Assert.Equal(published.Id, advanced.Predecessor);
        Assert.True(firstWriter.TryGetCurrentView(out var current));
        Assert.Same(published, current);

        register.Replace(published);

        await Assert.ThrowsAsync<ClusterServiceAuthorityException>(() => Publish(firstWriter, TestServiceMembership.B).AsTask());
        Assert.False(firstWriter.TryGetCurrentView(out _));
        Assert.Equal(targetRevision + 1, register.SuccessfulWrites);
    }

    [Fact(Timeout = 30_000)]
    public async Task RejectedPublicationPreservesItsObservedPredecessorHighWatermark()
    {
        var register = new TestServiceViewRegister();
        await using var firstWriter = Create(register, new());
        await using var secondWriter = Create(register, new());
        var first = await Publish(firstWriter, TestServiceMembership.A);
        await Publish(secondWriter, TestServiceMembership.B);
        var unknown = SiloAddress.FromParsableString("127.0.0.1:33333@9");
        await Assert.ThrowsAsync<ArgumentException>(() => Publish(firstWriter, unknown, [unknown]).AsTask());

        register.Replace(first);

        await Assert.ThrowsAsync<ClusterServiceAuthorityException>(() => firstWriter.RefreshAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.False(firstWriter.TryGetCurrentView(out _));
        Assert.Equal(2, register.SuccessfulWrites);
    }

    [Fact(Timeout = 30_000)]
    public async Task SkippedReadsExposeAuthoritativePredecessorRatherThanLastObservation()
    {
        var register = new TestServiceViewRegister();
        var membership = new TestServiceMembership();
        await using var writer = Create(register, membership);
        await using var reader = Create(register, membership);
        var first = await Publish(writer, TestServiceMembership.A);
        await reader.RefreshAsync(TestContext.Current.CancellationToken);
        var skipped = await Publish(writer, TestServiceMembership.B);
        var latest = await Publish(writer, TestServiceMembership.A);

        var observed = await reader.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(latest.Id, observed.Id);
        Assert.Equal(skipped.Id, observed.Predecessor);
        Assert.NotEqual(first.Id, observed.Predecessor);
        Assert.Equal(first.ResourceOwners[TestServiceMembership.Resource], observed.ResourceOwners[TestServiceMembership.Resource]);
        Assert.Same(observed, await reader.RefreshAsync(TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 30_000)]
    public async Task MinimumRefreshDoesNotReturnAnOlderViewAndCancellationIsCallerLocal()
    {
        var register = new TestServiceViewRegister();
        await using var provider = Create(register, new());
        var first = await Publish(provider, TestServiceMembership.A);
        var minimum = new RegisteredServiceViewId(first.Id.ServiceId, first.Id.AuthorityId, 2);
        using var cancellation = new CancellationTokenSource();
        var cancelled = provider.RefreshAtLeastAsync(minimum, cancellation.Token).AsTask();
        var pending = provider.RefreshAtLeastAsync(minimum, TestContext.Current.CancellationToken).AsTask();
        Assert.False(pending.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.False(pending.IsCompleted);

        var next = await Publish(provider, TestServiceMembership.B);

        Assert.Equal(next.Id, (await pending).Id);
        Assert.True(provider.TryGetCurrentView(out _));
    }

    [Fact(Timeout = 30_000)]
    public async Task CancelledCallerDoesNotLetAnOlderInflightReadRaceANewerRefresh()
    {
        var register = new TestServiceViewRegister();
        var membership = new TestServiceMembership();
        await using var reader = Create(register, membership);
        await using var writer = Create(register, membership);
        var first = await Publish(reader, TestServiceMembership.A);
        var oldReadCaptured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOldRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        register.AfterRead = async () =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                oldReadCaptured.SetResult();
                await releaseOldRead.Task.WaitAsync(TestContext.Current.CancellationToken);
            }
        };
        using var cancellation = new CancellationTokenSource();
        var oldRefresh = reader.RefreshAsync(cancellation.Token).AsTask();
        await oldReadCaptured.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = await Publish(writer, TestServiceMembership.B);
        var currentRefresh = reader.RefreshAtLeastAsync(second.Id, TestContext.Current.CancellationToken).AsTask();
        Assert.False(currentRefresh.IsCompleted);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldRefresh);
        Assert.False(currentRefresh.IsCompleted);
        releaseOldRead.SetResult();

        Assert.Equal(second.Id, (await currentRefresh).Id);
        Assert.True(reader.TryGetCurrentView(out var current));
        Assert.True(second.HasSameContent(current));
        Assert.Equal(first.Id, current.Predecessor);
    }

    [Fact(Timeout = 30_000)]
    public async Task DisposalTerminatesPendingRefreshAndSubscribers()
    {
        var register = new TestServiceViewRegister();
        var provider = Create(register, new());
        var first = await Publish(provider, TestServiceMembership.A);
        await using var stream = provider.ViewUpdates.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await stream.MoveNextAsync());
        var next = stream.MoveNextAsync().AsTask();
        var pending = provider.RefreshAtLeastAsync(new(first.Id.ServiceId, first.Id.AuthorityId, 99), TestContext.Current.CancellationToken).AsTask();

        await provider.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => next);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => provider.RefreshAtLeastAsync(first.Id, TestContext.Current.CancellationToken).AsTask());
        Assert.False(provider.TryGetCurrentView(out _));
        await provider.DisposeAsync();
    }

    [Fact(Timeout = 30_000)]
    public async Task RegisterFailureTerminatesRefreshAndStreamWithOriginalException()
    {
        var register = new TestServiceViewRegister();
        await using var provider = Create(register, new());
        var first = await Publish(provider, TestServiceMembership.A);
        await using var stream = provider.ViewUpdates.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await stream.MoveNextAsync());
        var next = stream.MoveNextAsync().AsTask();
        var pending = provider.RefreshAtLeastAsync(new(first.Id.ServiceId, first.Id.AuthorityId, 99), TestContext.Current.CancellationToken).AsTask();
        var failure = new IOException("Authority is unreachable.");
        register.Failure = failure;

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => provider.RefreshAsync(TestContext.Current.CancellationToken).AsTask()));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => pending));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => next));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => provider.RefreshAtLeastAsync(first.Id, TestContext.Current.CancellationToken).AsTask()));
        Assert.False(provider.TryGetCurrentView(out _));
    }

    [Theory(Timeout = 30_000)]
    [InlineData("authority")]
    [InlineData("service")]
    [InlineData("deleted")]
    [InlineData("regressed")]
    [InlineData("content")]
    [InlineData("recreated")]
    public async Task ChangedAuthorityDeletionRollbackAndConflictingIdentityRequireBootstrap(string change)
    {
        var register = new TestServiceViewRegister();
        await using var provider = Create(register, new());
        var first = await Publish(provider, TestServiceMembership.A);
        var second = await Publish(provider, TestServiceMembership.B);
        await provider.RefreshAsync(TestContext.Current.CancellationToken);
        var replacement = change switch
        {
            "authority" => MakeView(2, TestServiceMembership.B, authority: "replacement"),
            "service" => MakeView(2, TestServiceMembership.B, service: "other"),
            "deleted" => null,
            "regressed" => first,
            "content" => MakeView(second.Id.Revision, TestServiceMembership.A, predecessor: first.Id),
            "recreated" => second,
            _ => throw new InvalidOperationException()
        };
        register.Replace(replacement);

        await Assert.ThrowsAsync<ClusterServiceAuthorityException>(() => provider.RefreshAsync(TestContext.Current.CancellationToken).AsTask());

        Assert.False(provider.TryGetCurrentView(out _));
    }

    [Fact(Timeout = 30_000)]
    public async Task LivenessRefreshDoesNotRewriteTheAuthoritativeDeadOwner()
    {
        var register = new TestServiceViewRegister();
        var membership = new TestServiceMembership();
        await using var provider = Create(register, membership);
        var first = await Publish(provider, TestServiceMembership.A);
        membership.SetStatus(TestServiceMembership.A, SiloStatus.Dead);

        await provider.RefreshLivenessAsync(TestContext.Current.CancellationToken);
        var current = await provider.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Same(first, current);
        Assert.False(provider.IsOwnerLive(TestServiceMembership.A, current.MembershipWatermark));
        Assert.Equal(TestServiceMembership.A, current.ResourceOwners[TestServiceMembership.Resource]);
        Assert.Equal(1, register.SuccessfulWrites);
        var replacement = await Publish(provider, TestServiceMembership.B, [TestServiceMembership.B]);
        Assert.Equal(first.Id.Revision + 1, replacement.Id.Revision);
        Assert.Equal(TestServiceMembership.B, replacement.ResourceOwners[TestServiceMembership.Resource]);
    }

    [Fact(Timeout = 30_000)]
    public async Task LivenessRefreshEnforcesItsMinimumAndPreservesTheAuthoritativeAssignment()
    {
        var register = new TestServiceViewRegister();
        var membership = new TestServiceMembership();
        await using var provider = Create(register, membership);
        var view = await Publish(provider, TestServiceMembership.A);
        membership.SetStatus(TestServiceMembership.B, SiloStatus.Dead);

        await provider.RefreshLivenessAsync(new MembershipVersion(8), TestContext.Current.CancellationToken);

        Assert.Equal(new MembershipVersion(8), membership.LastRefreshMinimum);
        Assert.True(provider.TryGetCurrentView(out var current));
        Assert.Same(view, current);
        Assert.Equal(1, register.SuccessfulWrites);
        await Assert.ThrowsAsync<ClusterServiceViewUnavailableException>(() =>
            provider.RefreshLivenessAsync(new MembershipVersion(9), TestContext.Current.CancellationToken).AsTask());
        Assert.False(provider.TryGetCurrentView(out _));
        Assert.Equal(1, register.SuccessfulWrites);
    }

    [Fact(Timeout = 30_000)]
    public async Task LivenessRefreshDisposalCancelsPendingWorkAndRejectsSatisfiedMinimum()
    {
        var membership = new TestServiceMembership();
        await using var provider = Create(new(), membership);
        var view = await Publish(provider, TestServiceMembership.A);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingMembership = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken sharedToken = default;
        membership.OnRefresh = async (_, token) =>
        {
            sharedToken = token;
            started.TrySetResult();
            await pendingMembership.Task.WaitAsync(token);
        };
        var refresh = provider.RefreshLivenessAsync(TestContext.Current.CancellationToken).AsTask();
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);

        await provider.DisposeAsync();

        Assert.True(sharedToken.IsCancellationRequested);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => refresh);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            provider.RefreshLivenessAsync(view.MembershipWatermark, TestContext.Current.CancellationToken).AsTask());
        Assert.False(provider.IsOwnerLive(TestServiceMembership.A, view.MembershipWatermark));
    }

    [Fact(Timeout = 30_000)]
    public async Task LivenessCallerCancellationLeavesTheProviderActive()
    {
        var membership = new TestServiceMembership();
        await using var provider = Create(new(), membership);
        var view = await Publish(provider, TestServiceMembership.A);
        var pendingMembership = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        membership.OnRefresh = (_, token) => new(pendingMembership.Task.WaitAsync(token));
        using var cancellation = new CancellationTokenSource();
        var refresh = provider.RefreshLivenessAsync(cancellation.Token).AsTask();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        Assert.True(provider.TryGetCurrentView(out var current));
        Assert.Same(view, current);
        membership.OnRefresh = null;
        await provider.RefreshLivenessAsync(view.MembershipWatermark, TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 30_000)]
    public async Task LivenessFailurePreservesTheOriginalExceptionForPendingRefreshAndStream()
    {
        var membership = new TestServiceMembership();
        await using var provider = Create(new(), membership);
        var view = await Publish(provider, TestServiceMembership.A);
        await using var updates = provider.ViewUpdates.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await updates.MoveNextAsync());
        var next = updates.MoveNextAsync().AsTask();
        var refresh = provider.RefreshAtLeastAsync(new(view.Id.ServiceId, view.Id.AuthorityId, 99), TestContext.Current.CancellationToken).AsTask();
        var failure = new IOException("Membership refresh failed.");
        membership.OnRefresh = (_, _) => ValueTask.FromException(failure);

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() =>
            provider.RefreshLivenessAsync(TestContext.Current.CancellationToken).AsTask()));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => refresh));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => next));
        Assert.False(provider.TryGetCurrentView(out _));
    }

    [Fact(Timeout = 30_000)]
    public async Task LivenessFailureCancelsActiveRegisterReadAndFailsQueuedOperationsWithOriginalException()
    {
        var register = new TestServiceViewRegister();
        var membership = new TestServiceMembership();
        await using var provider = Create(register, membership);
        await Publish(provider, TestServiceMembership.A);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        register.AfterRead = () =>
        {
            entered.TrySetResult();
            return release.Task;
        };
        var activeRead = provider.RefreshAsync(TestContext.Current.CancellationToken).AsTask();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var queuedRead = provider.RefreshAsync(TestContext.Current.CancellationToken).AsTask();
        var queuedWrite = Publish(provider, TestServiceMembership.B).AsTask();
        Assert.False(activeRead.IsCompleted);
        Assert.False(queuedRead.IsCompleted);
        Assert.False(queuedWrite.IsCompleted);
        var failure = new IOException("Liveness authority failed during a register read.");
        membership.OnRefresh = (_, _) => ValueTask.FromException(failure);

        try
        {
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() =>
                provider.RefreshLivenessAsync(TestContext.Current.CancellationToken).AsTask()));
            foreach (var pending in new Task[] { activeRead, queuedRead, queuedWrite })
            {
                Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() =>
                    pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)));
            }

            Assert.False(release.Task.IsCompleted);
            Assert.False(provider.TryGetCurrentView(out _));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task PublicationRejectsNonmembersDeadAndIneligibleParticipants()
    {
        var register = new TestServiceViewRegister();
        var membership = new TestServiceMembership();
        await using var provider = new RegisteredClusterServiceViewProvider(
            "service", "authority", register, membership, member => member.SiloAddress.Equals(TestServiceMembership.A), TimeSpan.FromDays(1));
        await Assert.ThrowsAsync<ArgumentException>(() => Publish(provider, TestServiceMembership.B).AsTask());
        var unknown = SiloAddress.FromParsableString("127.0.0.1:33333@9");
        await Assert.ThrowsAsync<ArgumentException>(() => Publish(provider, unknown, [unknown]).AsTask());
        membership.SetStatus(TestServiceMembership.A, SiloStatus.Dead);
        await Assert.ThrowsAsync<ArgumentException>(() => Publish(provider, TestServiceMembership.A, [TestServiceMembership.A]).AsTask());
        Assert.Equal(0, register.SuccessfulWrites);
    }

    [Fact]
    public void ViewCopiesCanonicalIndexesAndRejectsDuplicateMissingOrUndeclaredAssignments()
    {
        var participants = new[] { TestServiceMembership.B, TestServiceMembership.A };
        var resources = new[] { TestServiceMembership.Resource };
        var assignments = new[] { KeyValuePair.Create(TestServiceMembership.Resource, TestServiceMembership.A) };
        var view = new RegisteredClusterServiceView(new("service", "authority", 1), null, new(7), Configuration, participants, resources, assignments);
        participants[0] = TestServiceMembership.A;
        resources[0] = "changed";
        assignments[0] = KeyValuePair.Create("changed", TestServiceMembership.B);

        Assert.Equal(new[] { TestServiceMembership.A, TestServiceMembership.B }, view.Participants);
        Assert.Equal([TestServiceMembership.Resource], view.Resources);
        Assert.Equal(TestServiceMembership.A, view.ResourceOwners[TestServiceMembership.Resource]);
        Assert.Equal([TestServiceMembership.Resource], view.GetOwnedResources(TestServiceMembership.A));
        Assert.Throws<ArgumentException>(() => MakeInvalid([TestServiceMembership.Resource], []));
        Assert.Throws<ArgumentException>(() => MakeInvalid([], [KeyValuePair.Create(TestServiceMembership.Resource, TestServiceMembership.A)]));
        Assert.Throws<ArgumentException>(() => MakeInvalid([TestServiceMembership.Resource],
            [KeyValuePair.Create(TestServiceMembership.Resource, TestServiceMembership.A), KeyValuePair.Create(TestServiceMembership.Resource, TestServiceMembership.B)]));

        static RegisteredClusterServiceView MakeInvalid(string[] catalog, KeyValuePair<string, SiloAddress>[] mapping) =>
            new(new("service", "authority", 1), null, new(7), Configuration,
                [TestServiceMembership.A, TestServiceMembership.B], catalog, mapping);
    }

    [Fact(Timeout = 30_000)]
    public async Task ExplicitRingPublicationRejectsGapsOverlapAndIneligibleOwners()
    {
        var register = new TestServiceViewRegister();
        await using var provider = Create(register, new());
        foreach (var (participants, assignments) in new (SiloAddress[], ImmutableArray<ClusterServicePartitionAssignment>)[]
        {
            ([TestServiceMembership.A], [new(TestServiceMembership.A, 0, RingRange.Create(0, 100))]),
            ([TestServiceMembership.A, TestServiceMembership.B], [new(TestServiceMembership.A, 0, RingRange.Create(0, 200)), new(TestServiceMembership.B, 0, RingRange.Create(100, 0))]),
            ([TestServiceMembership.A], [new(TestServiceMembership.B, 0, RingRange.Full)])
        })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => provider.TryPublishAsync(
                Configuration, participants, [], [], TestContext.Current.CancellationToken, assignments).AsTask());
        }

        var view = await provider.TryPublishAsync(
            Configuration, [TestServiceMembership.A, TestServiceMembership.B], ["partition-0", "partition-1"],
            [KeyValuePair.Create("partition-0", TestServiceMembership.A), KeyValuePair.Create("partition-1", TestServiceMembership.B)],
            TestContext.Current.CancellationToken,
            [new(TestServiceMembership.A, 0, RingRange.Create(0, 100)), new(TestServiceMembership.B, 0, RingRange.Create(100, 0))],
            [KeyValuePair.Create("partition-0", RingRange.Create(0, 100)), KeyValuePair.Create("partition-1", RingRange.Create(100, 0))]);
        Assert.NotNull(view);
        Assert.NotNull(view.Topology);
        Assert.Equal(2, view.RingAssignments.Length);
        Assert.Equal(1, register.SuccessfulWrites);
    }

    [Fact(Timeout = 30_000)]
    public async Task RegisteredRingMappingRetainsServiceScopedResourceIdsAcrossOwnerChanges()
    {
        var register = new TestServiceViewRegister();
        var membership = new TestServiceMembership();
        await using var writer = Create(register, membership);
        await using var observer = Create(register, membership);
        var ranges = new Dictionary<string, RingRange>
        {
            ["partition-0"] = RingRange.Create(0, 100),
            ["partition-1"] = RingRange.Create(100, 0)
        };
        var first = (await writer.TryPublishAsync(Configuration, [TestServiceMembership.B, TestServiceMembership.A], ranges.Keys,
            [KeyValuePair.Create("partition-0", TestServiceMembership.A), KeyValuePair.Create("partition-1", TestServiceMembership.B)],
            TestContext.Current.CancellationToken,
            [new(TestServiceMembership.A, 0, ranges["partition-0"]), new(TestServiceMembership.B, 0, ranges["partition-1"])], ranges))!;
        var second = (await writer.TryPublishAsync(Configuration, [TestServiceMembership.A, TestServiceMembership.B], ranges.Keys,
            [KeyValuePair.Create("partition-0", TestServiceMembership.B), KeyValuePair.Create("partition-1", TestServiceMembership.A)],
            TestContext.Current.CancellationToken,
            [new(TestServiceMembership.B, 0, ranges["partition-0"]), new(TestServiceMembership.A, 0, ranges["partition-1"])], ranges))!;
        var current = await observer.RefreshAsync(TestContext.Current.CancellationToken);

        foreach (var point in new uint[] { 0, 1, 100, 101, uint.MaxValue })
        {
            var resourceId = Assert.Single(ranges, entry => entry.Value.Contains(point)).Key;
            Assert.True(first.TryGetRingResource(point, out var previousResource, out var previousOwner));
            Assert.True(current.TryGetRingResource(point, out var resource, out var owner));
            Assert.Equal(resourceId, previousResource);
            Assert.Equal(resourceId, resource);
            Assert.Equal(current.ResourceOwners[resourceId], owner);
            Assert.NotEqual(previousOwner, owner);
            Assert.Equal(ranges[resourceId], current.ResourcePartitions[resourceId].Range);
        }

        Assert.True(second.HasSameContent(current));
        Assert.Equal(first.Id, current.Predecessor);
        Assert.Same(membership.InitialSnapshot, membership.CurrentSnapshot);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("wrong-owner")]
    [InlineData("undeclared")]
    public void RingResourceAssociationsRejectMissingDuplicateMismatchedAndUndeclaredIds(string invalid)
    {
        var ranges = new Dictionary<string, RingRange>
        {
            ["partition-0"] = RingRange.Create(0, 100),
            ["partition-1"] = RingRange.Create(100, 0)
        };
        if (invalid == "missing")
        {
            ranges.Remove("partition-1");
        }
        else if (invalid == "duplicate")
        {
            ranges["partition-1"] = ranges["partition-0"];
        }
        else if (invalid == "wrong-owner")
        {
            (ranges["partition-0"], ranges["partition-1"]) = (ranges["partition-1"], ranges["partition-0"]);
        }
        else
        {
            ranges.Remove("partition-1");
            ranges.Add("undeclared", RingRange.Create(100, 0));
        }

        Assert.Throws<ArgumentException>(() => new RegisteredClusterServiceView(new("service", "authority", 1), null, new(7),
            Configuration, [TestServiceMembership.A, TestServiceMembership.B], ["partition-0", "partition-1"],
            [KeyValuePair.Create("partition-0", TestServiceMembership.A), KeyValuePair.Create("partition-1", TestServiceMembership.B)],
            [new(TestServiceMembership.A, 0, RingRange.Create(0, 100)), new(TestServiceMembership.B, 0, RingRange.Create(100, 0))], ranges));
    }

    [Fact]
    public void IdentityOrderingNeverOrdersUnrelatedNamespaces()
    {
        var id = new RegisteredServiceViewId("service", "authority", 1);
        Assert.True(id.CompareTo(new("service", "authority", 2)) < 0);
        Assert.Throws<ClusterServiceAuthorityException>(() => id.CompareTo(new("other", "authority", 1)));
        Assert.Throws<ClusterServiceAuthorityException>(() => id.CompareTo(new("service", "other", 1)));
    }

    [Fact]
    public void BootstrapSentinelIsNotAnAuthoritativePublishedViewOrPredecessor()
    {
        var bootstrap = new RegisteredServiceViewId("service", "authority", -1);
        var published = MakeView(1, TestServiceMembership.A);
        Assert.True(published.Id.CompareTo(bootstrap) > 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegisteredClusterServiceView(
            bootstrap, null, new(7), Configuration, [], [], []));
        Assert.Throws<ArgumentException>(() => new RegisteredClusterServiceView(
            published.Id, bootstrap, new(7), Configuration, [], [], []));
    }

    internal static RegisteredServiceConfiguration Configuration { get; } = new(1, "qualified-resource-v1", "{\"concurrency\":1}");

    internal static RegisteredClusterServiceViewProvider Create(TestServiceViewRegister register, TestServiceMembership membership) =>
        new("service", "authority", register, membership, pollInterval: TimeSpan.FromDays(1));

    internal static async ValueTask<RegisteredClusterServiceView> Publish(
        RegisteredClusterServiceViewProvider provider, SiloAddress owner, SiloAddress[]? participants = null) =>
        (await provider.TryPublishAsync(Configuration, participants ?? [TestServiceMembership.A, TestServiceMembership.B],
            [TestServiceMembership.Resource], [KeyValuePair.Create(TestServiceMembership.Resource, owner)], TestContext.Current.CancellationToken))!;

    internal static RegisteredClusterServiceView MakeView(
        long revision, SiloAddress owner, RegisteredServiceViewId? predecessor = null, string service = "service", string authority = "authority") =>
        new(new(service, authority, revision), predecessor, new(7), Configuration,
            [TestServiceMembership.A, TestServiceMembership.B], [TestServiceMembership.Resource],
            [KeyValuePair.Create(TestServiceMembership.Resource, owner)]);
}

internal sealed class TestServiceViewRegister : IClusterServiceViewRegister
{
    private readonly object _lock = new();
    private ClusterServiceRegisterRead _current = new(null, null);
    private int _writes;

    public Func<Task>? AfterRead { get; set; }
    public Func<RegisteredClusterServiceView, Task>? AfterWrite { get; set; }
    public Exception? Failure { get; set; }
    public int SuccessfulWrites => _writes;
    public ClusterServiceRegisterRead Current => _current;

    public async ValueTask<ClusterServiceRegisterRead> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Failure is { } exception)
        {
            throw exception;
        }

        ClusterServiceRegisterRead read;
        lock (_lock)
        {
            read = _current;
        }

        if (AfterRead is { } afterRead)
        {
            await afterRead().WaitAsync(cancellationToken);
        }

        return read;
    }

    public async ValueTask<string?> TryWriteAsync(RegisteredClusterServiceView view, string? expectedToken, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string token;
        lock (_lock)
        {
            if (!StringComparer.Ordinal.Equals(_current.Token, expectedToken))
            {
                return null;
            }

            token = $"opaque-etag-{++_writes}";
            _current = new(view, token);
        }

        if (AfterWrite is { } afterWrite)
        {
            await afterWrite(view).WaitAsync(cancellationToken);
        }

        return token;
    }

    public void Replace(RegisteredClusterServiceView? view)
    {
        lock (_lock)
        {
            _current = new(view, view is null ? null : "replacement-etag");
        }
    }
}

internal sealed class TestServiceMembership : IClusterMembershipService
{
    public static SiloAddress A { get; } = SiloAddress.FromParsableString("127.0.0.1:11111@1");
    public static SiloAddress B { get; } = SiloAddress.FromParsableString("127.0.0.1:22222@2");
    public const string Resource = "namespace.servicebus.windows.net/hub/consumer-group/0";
    public ClusterMembershipSnapshot InitialSnapshot { get; } = new(
        ImmutableDictionary<SiloAddress, ClusterMember>.Empty.Add(A, new(A, SiloStatus.Active, "A")).Add(B, new(B, SiloStatus.Active, "B")), new(7));

    public TestServiceMembership() => CurrentSnapshot = InitialSnapshot;
    public ClusterMembershipSnapshot CurrentSnapshot { get; private set; }
    public Func<MembershipVersion, CancellationToken, ValueTask>? OnRefresh { get; set; }
    public MembershipVersion LastRefreshMinimum { get; private set; }
    public IAsyncEnumerable<ClusterMembershipSnapshot> MembershipUpdates => throw new NotSupportedException();
    public Task<bool> TryKill(SiloAddress siloAddress) => throw new NotSupportedException();
    public ValueTask Refresh(MembershipVersion minimumVersion = default, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastRefreshMinimum = minimumVersion;
        return OnRefresh?.Invoke(minimumVersion, cancellationToken) ?? ValueTask.CompletedTask;
    }

    public void SetStatus(SiloAddress silo, SiloStatus status) =>
        CurrentSnapshot = new(CurrentSnapshot.Members.SetItem(silo, new(silo, status, silo.ToParsableString())), new(CurrentSnapshot.Version.Value + 1));
}
