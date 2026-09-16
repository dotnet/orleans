using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orleans.Core;
using Orleans.Runtime;
using Orleans.Streams;
using TestExtensions;
using Xunit;

namespace UnitTests.StreamingTests;

[TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
[TestCategory("BVT"), TestCategory("Streaming")]
public sealed class PullingAgentPublisherRegistryTests
{
    private static readonly TimeSpan PhaseTimeout = TimeSpan.FromSeconds(30);
    private static readonly QualifiedStreamId First = new("publisher", StreamId.Create("streams", "first"));
    private static readonly QualifiedStreamId Second = new("publisher", StreamId.Create("streams", "second"));
    private static readonly QualifiedStreamId Third = new("publisher", StreamId.Create("streams", "third"));

    [Fact]
    public async Task RegisterProducer_EachFirstRegistration_WaitsForDurableIntentCommit()
    {
        var setup = new Setup();
        await setup.Registry.Load(TestContext.Current.CancellationToken);
        QualifiedStreamId[] streams = [First, new("other-provider", First.StreamId)];
        var committed = new HashSet<QualifiedStreamId>();

        foreach (var stream in streams)
        {
            var writeEntered = NewSignal();
            var releaseWrite = NewSignal();
            setup.Storage.BeforeWrite = async () =>
            {
                writeEntered.SetResult();
                await releaseWrite.Task;
            };
            var registering = setup.Registry.RegisterProducer(stream, TestContext.Current.CancellationToken);

            try
            {
                await AwaitPhase(writeEntered.Task, $"intent write entered for {stream}");
                Assert.False(registering.IsCompleted);
                AssertStreams(committed, setup.Store.Streams);
                Assert.Contains(stream, setup.Storage.State.Streams);
                Assert.Equal(committed.Count, setup.Registrations.Count);
            }
            finally
            {
                releaseWrite.TrySetResult();
                await AwaitPhase(registering, $"registration after committing intent for {stream}");
            }

            Assert.Same(setup.Subscriptions, await registering);
            committed.Add(stream);
            AssertStreams(committed, setup.Store.Streams);
            var registration = setup.Registrations[^1];
            Assert.Equal(stream, registration.Stream);
            Assert.Equal(setup.Producer, registration.Producer);
            Assert.Equal(CancellationToken.None, registration.CancellationToken);
            AssertStreams(committed, registration.DurableStreams);
        }

        Assert.Equal(2, setup.Storage.WriteAttempts.Count);
        AssertStreams([streams[0]], setup.Storage.WriteAttempts[0]);
        AssertStreams(streams, setup.Storage.WriteAttempts[1]);
    }

    [Fact]
    public async Task RegisterProducer_CallerCanceled_DrainAndRetireWaitForRawRpcAndRejectLateRegistrations()
    {
        var setup = new Setup();
        await setup.Registry.Load(TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var rpcEntered = NewSignal();
        var releaseRpc = NewSignal();
        setup.BeforeRegister = _ =>
        {
            rpcEntered.SetResult();
            return releaseRpc.Task;
        };
        var registering = setup.Registry.RegisterProducer(First, cancellation.Token);
        Task draining = Task.CompletedTask;
        Task retiring = Task.CompletedTask;

        try
        {
            await AwaitPhase(rpcEntered.Task, "raw registration RPC entered");
            cancellation.Cancel();
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                AwaitPhase(registering, "caller registration wait canceled"));
            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.False(releaseRpc.Task.IsCompleted);

            draining = setup.Registry.Drain();
            retiring = setup.Registry.Retire();
            Assert.False(draining.IsCompleted);
            Assert.False(retiring.IsCompleted);
            Assert.Empty(setup.Unregistrations);
            AssertStreams([First], setup.Store.Streams);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                setup.Registry.RegisterProducer(Second, CancellationToken.None));
            Assert.Single(setup.Registrations);
            Assert.Single(setup.Storage.WriteAttempts);
        }
        finally
        {
            releaseRpc.TrySetResult();
            await AwaitPhase(setup.Registry.Drain(), "draining the released raw registration RPC");
            await AwaitPhase(retiring, "retirement after raw registration completion");
        }

        Assert.Equal(["register-start", "register-complete", "unregister"], setup.Operations.Select(operation => operation.Name));
        Assert.Equal(First, Assert.Single(setup.Unregistrations).Stream);
        Assert.Empty(setup.ActivePublishers);
        Assert.Empty(setup.Store.Streams);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            setup.Registry.RegisterProducer(First, CancellationToken.None));
        Assert.Single(setup.Registrations);
        Assert.Equal(2, setup.Storage.WriteAttempts.Count);
    }

    [Fact]
    public async Task Retire_RegistrationStillWritingIntent_WaitsAndLeavesNoLatePublisher()
    {
        var setup = new Setup();
        await setup.Registry.Load(TestContext.Current.CancellationToken);
        var writeEntered = NewSignal();
        var releaseWrite = NewSignal();
        setup.Storage.BeforeWrite = () =>
        {
            writeEntered.TrySetResult();
            return releaseWrite.Task;
        };
        var registering = setup.Registry.RegisterProducer(First, CancellationToken.None);
        Task retiring = Task.CompletedTask;

        try
        {
            await AwaitPhase(writeEntered.Task, "admitted registration waiting on durable intent");
            retiring = setup.Registry.Retire();
            Assert.False(retiring.IsCompleted);
            Assert.Empty(setup.Registrations);
            Assert.Empty(setup.Unregistrations);
            Assert.Empty(setup.Store.Streams);
        }
        finally
        {
            releaseWrite.TrySetResult();
            await AwaitPhase(registering, "admitted registration after releasing intent write");
            await AwaitPhase(retiring, "retiring the admitted publisher");
        }

        Assert.Equal(["register-start", "register-complete", "unregister"], setup.Operations.Select(operation => operation.Name));
        Assert.Empty(setup.ActivePublishers);
        Assert.Empty(setup.Store.Streams);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            setup.Registry.RegisterProducer(Second, CancellationToken.None));
        Assert.Single(setup.Registrations);
        Assert.Single(setup.Unregistrations);
        Assert.Equal(2, setup.Storage.WriteAttempts.Count);
    }

    [Fact]
    public async Task Load_NewRegistryInstance_RetiresPreviouslyPersistedStreams()
    {
        var setup = new Setup();
        await setup.Registry.Load(TestContext.Current.CancellationToken);
        await setup.Registry.RegisterProducer(First, CancellationToken.None);
        await setup.Registry.RegisterProducer(Second, CancellationToken.None);
        await setup.Registry.Drain();
        var reloadedStorage = new SnapshotStorage(setup.Store);
        var reloaded = setup.CreateRegistry(reloadedStorage);
        Assert.Empty(reloadedStorage.State.Streams);

        await reloaded.Load(TestContext.Current.CancellationToken);

        Assert.Equal(1, reloadedStorage.ReadCount);
        AssertStreams([First, Second], reloadedStorage.State.Streams);
        Assert.NotSame(setup.Storage.State.Streams, reloadedStorage.State.Streams);
        Assert.NotSame(setup.Store.Streams, reloadedStorage.State.Streams);
        await reloaded.Retire();

        AssertStreams([First, Second], setup.Unregistrations.Select(call => call.Stream));
        Assert.Equal(2, setup.Registrations.Count);
        Assert.Equal(2, reloadedStorage.WriteAttempts.Count);
        Assert.Empty(setup.Store.Streams);
        Assert.Empty(setup.ActivePublishers);
    }

    [Fact]
    public async Task RegisterProducer_DuplicateStream_DoesNotWriteAnotherIntent()
    {
        var setup = new Setup();
        await setup.Registry.Load(TestContext.Current.CancellationToken);

        var first = await setup.Registry.RegisterProducer(First, CancellationToken.None);
        var duplicate = await setup.Registry.RegisterProducer(First, CancellationToken.None);

        Assert.Same(setup.Subscriptions, first);
        Assert.Same(first, duplicate);
        AssertStreams([First], Assert.Single(setup.Storage.WriteAttempts));
        AssertStreams([First], setup.Store.Streams);
        Assert.Equal([First, First], setup.Registrations.Select(call => call.Stream));
        Assert.All(setup.Registrations, call => AssertStreams([First], call.DurableStreams));
    }

    [Fact]
    public async Task Retire_PartialUnregisterFailure_RetainsRemainingIntentsAndRetrySucceeds()
    {
        var setup = new Setup();
        await setup.RegisterAll();
        var failure = new InvalidOperationException("Unregister failed.");
        setup.BeforeUnregister = _ => setup.Unregistrations.Count == 2
            ? Task.FromException(failure)
            : Task.CompletedTask;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Registry.Retire());

        Assert.Same(failure, exception);
        Assert.Equal(2, setup.Unregistrations.Count);
        var removed = setup.Unregistrations[0].Stream;
        var failed = setup.Unregistrations[1].Stream;
        var remaining = new HashSet<QualifiedStreamId>([First, Second, Third]);
        remaining.Remove(removed);
        Assert.Contains(failed, remaining);
        AssertStreams(remaining, setup.Store.Streams);
        AssertStreams(remaining, setup.ActivePublishers);
        Assert.Equal(4, setup.Storage.WriteAttempts.Count);
        Assert.Equal(1, setup.Storage.ReadCount);
        setup.BeforeUnregister = null;

        await setup.Registry.Retire();

        Assert.Equal(2, setup.Storage.ReadCount);
        Assert.Equal(4, setup.Unregistrations.Count);
        AssertStreams(remaining, setup.Unregistrations.Skip(2).Select(call => call.Stream));
        Assert.Single(setup.Unregistrations, call => call.Stream.Equals(removed));
        Assert.Equal(2, setup.Unregistrations.Count(call => call.Stream.Equals(failed)));
        Assert.Equal(6, setup.Storage.WriteAttempts.Count);
        Assert.Empty(setup.Store.Streams);
        Assert.Empty(setup.ActivePublishers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisterProducer_AmbiguousIntentWrite_RereadsBeforeRetryAndPreservesPriorStreams(bool commitBeforeThrow)
    {
        var setup = new Setup();
        await setup.Registry.Load(TestContext.Current.CancellationToken);
        await setup.Registry.RegisterProducer(First, CancellationToken.None);
        await setup.Registry.RegisterProducer(Second, CancellationToken.None);
        var failure = setup.Storage.FailNextWrite(commitBeforeThrow);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            setup.Registry.RegisterProducer(Third, CancellationToken.None));

        Assert.Same(failure, exception);
        Assert.Equal([First, Second], setup.Registrations.Select(call => call.Stream));
        AssertStreams(commitBeforeThrow ? [First, Second, Third] : [First, Second], setup.Store.Streams);
        AssertStreams([First, Second, Third], setup.Storage.State.Streams);
        Assert.Equal(3, setup.Storage.WriteAttempts.Count);
        Assert.Equal(1, setup.Storage.ReadCount);

        Assert.Same(setup.Subscriptions, await setup.Registry.RegisterProducer(Third, CancellationToken.None));

        Assert.Equal(2, setup.Storage.ReadCount);
        Assert.Equal(commitBeforeThrow ? 3 : 4, setup.Storage.WriteAttempts.Count);
        Assert.Equal([First, Second, Third], setup.Registrations.Select(call => call.Stream));
        AssertStreams([First, Second, Third], setup.Registrations[^1].DurableStreams);
        AssertStreams([First, Second, Third], setup.Store.Streams);
        await setup.Registry.Retire();
        AssertStreams([First, Second, Third], setup.Unregistrations.Select(call => call.Stream));
        Assert.Empty(setup.Store.Streams);
        Assert.Empty(setup.ActivePublishers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retire_AmbiguousIntentWrite_RereadsAndRetiresEveryCommittedIntent(bool commitBeforeThrow)
    {
        var setup = new Setup();
        await setup.Registry.Load(TestContext.Current.CancellationToken);
        await setup.Registry.RegisterProducer(First, CancellationToken.None);
        var failure = setup.Storage.FailNextWrite(commitBeforeThrow);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            setup.Registry.RegisterProducer(Second, CancellationToken.None)));

        await setup.Registry.Retire();

        Assert.Equal(2, setup.Storage.ReadCount);
        Assert.Equal(First, Assert.Single(setup.Registrations).Stream);
        AssertStreams(commitBeforeThrow ? [First, Second] : [First], setup.Unregistrations.Select(call => call.Stream));
        Assert.Equal(commitBeforeThrow ? 4 : 3, setup.Storage.WriteAttempts.Count);
        Assert.Empty(setup.Store.Streams);
        Assert.Empty(setup.ActivePublishers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retire_AmbiguousRemovalWrite_RereadsAndPreservesAllRemainingIntents(bool commitBeforeThrow)
    {
        var setup = new Setup();
        await setup.RegisterAll();
        var failure = setup.Storage.FailNextWrite(commitBeforeThrow);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Registry.Retire());

        Assert.Same(failure, exception);
        var removed = Assert.Single(setup.Unregistrations).Stream;
        var remaining = new HashSet<QualifiedStreamId>([First, Second, Third]);
        remaining.Remove(removed);
        AssertStreams(remaining, setup.Storage.State.Streams);
        AssertStreams(remaining, setup.ActivePublishers);
        AssertStreams(commitBeforeThrow ? remaining : [First, Second, Third], setup.Store.Streams);
        Assert.Equal(4, setup.Storage.WriteAttempts.Count);
        Assert.Equal(1, setup.Storage.ReadCount);
        var durableBeforeRetry = new HashSet<QualifiedStreamId>(setup.Store.Streams);

        await setup.Registry.Retire();

        Assert.Equal(2, setup.Storage.ReadCount);
        AssertStreams(durableBeforeRetry, setup.Unregistrations.Skip(1).Select(call => call.Stream));
        Assert.Equal(commitBeforeThrow ? 3 : 4, setup.Unregistrations.Count);
        Assert.Equal(commitBeforeThrow ? 1 : 2, setup.Unregistrations.Count(call => call.Stream.Equals(removed)));
        Assert.Equal(commitBeforeThrow ? 6 : 7, setup.Storage.WriteAttempts.Count);
        Assert.Empty(setup.Store.Streams);
        Assert.Empty(setup.ActivePublishers);
    }

    [Fact]
    public async Task RegisterProducer_CanceledBeforeAdmission_CreatesNoIntentOrRpc()
    {
        var setup = new Setup();
        await setup.Registry.Load(TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            _ = setup.Registry.RegisterProducer(First, cancellation.Token);
        });

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Empty(setup.Storage.WriteAttempts);
        Assert.Empty(setup.Storage.State.Streams);
        Assert.Empty(setup.Store.Streams);
        Assert.Empty(setup.Operations);
        await setup.Registry.Retire();
        Assert.Empty(setup.Storage.WriteAttempts);
        Assert.Empty(setup.Operations);
        Assert.Equal(1, setup.Storage.ReadCount);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task AwaitPhase(Task task, string phase)
    {
        try
        {
            await task.WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"Timed out during publisher registry phase: {phase}.", exception);
        }
    }

    private static void AssertStreams(IEnumerable<QualifiedStreamId> expected, IEnumerable<QualifiedStreamId> actual)
        => Assert.Equal(expected.OrderBy(stream => stream), actual.OrderBy(stream => stream));

    private sealed class Setup
    {
        public DurableStore Store { get; } = new();
        public SnapshotStorage Storage { get; }
        public GrainId Producer { get; } = GrainId.Create("publisher-agent", "queue");
        public IStreamPubSub PubSub { get; } = Substitute.For<IStreamPubSub>();
        public ISet<PubSubSubscriptionState> Subscriptions { get; } = new HashSet<PubSubSubscriptionState>();
        public PullingAgentPublisherRegistry Registry { get; }
        public List<PubSubCall> Registrations { get; } = [];
        public List<PubSubCall> Unregistrations { get; } = [];
        public List<(string Name, QualifiedStreamId Stream)> Operations { get; } = [];
        public HashSet<QualifiedStreamId> ActivePublishers { get; } = [];
        public Func<QualifiedStreamId, Task>? BeforeRegister { get; set; }
        public Func<QualifiedStreamId, Task>? BeforeUnregister { get; set; }

        public Setup()
        {
            Storage = new(Store);
            Registry = CreateRegistry(Storage);
            PubSub.RegisterProducer(Arg.Any<QualifiedStreamId>(), Arg.Any<GrainId>(), Arg.Any<CancellationToken>())
                .Returns(call => Register(call.Arg<QualifiedStreamId>(), call.Arg<GrainId>(), call.Arg<CancellationToken>()));
            PubSub.UnregisterProducer(Arg.Any<QualifiedStreamId>(), Arg.Any<GrainId>(), Arg.Any<CancellationToken>())
                .Returns(call => Unregister(call.Arg<QualifiedStreamId>(), call.Arg<GrainId>(), call.Arg<CancellationToken>()));
        }

        public PullingAgentPublisherRegistry CreateRegistry(SnapshotStorage storage)
            => new(storage, PubSub, Producer, NullLogger.Instance);

        public async Task RegisterAll()
        {
            await Registry.Load(TestContext.Current.CancellationToken);
            foreach (var stream in new[] { First, Second, Third })
            {
                await Registry.RegisterProducer(stream, CancellationToken.None);
            }
        }

        private async Task<ISet<PubSubSubscriptionState>> Register(QualifiedStreamId stream, GrainId producer, CancellationToken cancellationToken)
        {
            Registrations.Add(new(stream, producer, cancellationToken, new(Store.Streams)));
            Operations.Add(("register-start", stream));
            Assert.Contains(stream, Store.Streams);
            Assert.Equal(Producer, producer);
            Assert.Equal(CancellationToken.None, cancellationToken);
            if (BeforeRegister is { } beforeRegister)
            {
                await beforeRegister(stream);
            }

            ActivePublishers.Add(stream);
            Operations.Add(("register-complete", stream));
            return Subscriptions;
        }

        private async Task Unregister(QualifiedStreamId stream, GrainId producer, CancellationToken cancellationToken)
        {
            Unregistrations.Add(new(stream, producer, cancellationToken, new(Store.Streams)));
            Operations.Add(("unregister", stream));
            Assert.Equal(Producer, producer);
            Assert.Equal(CancellationToken.None, cancellationToken);
            if (BeforeUnregister is { } beforeUnregister)
            {
                await beforeUnregister(stream);
            }

            ActivePublishers.Remove(stream);
        }
    }

    private sealed record PubSubCall(
        QualifiedStreamId Stream,
        GrainId Producer,
        CancellationToken CancellationToken,
        HashSet<QualifiedStreamId> DurableStreams);

    private sealed class DurableStore
    {
        public HashSet<QualifiedStreamId> Streams { get; set; } = [];
    }

    private sealed class SnapshotStorage(DurableStore store) : IStorage<PullingAgentPublisherState>
    {
        private InvalidOperationException? _writeFailure;
        private bool _commitBeforeThrow;

        public PullingAgentPublisherState State { get; set; } = new();
        public string? Etag => null;
        public bool RecordExists { get; private set; }
        public int ReadCount { get; private set; }
        public List<HashSet<QualifiedStreamId>> WriteAttempts { get; } = [];
        public Func<Task>? BeforeWrite { get; set; }

        public InvalidOperationException FailNextWrite(bool commitBeforeThrow)
        {
            _commitBeforeThrow = commitBeforeThrow;
            return _writeFailure = new InvalidOperationException(commitBeforeThrow
                ? "Storage committed, but acknowledgment failed."
                : "Storage failed before committing.");
        }

        public Task ReadStateAsync() => ReadStateAsync(CancellationToken.None);

        public Task ReadStateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            State = new() { Streams = new(store.Streams) };
            return Task.CompletedTask;
        }

        public Task WriteStateAsync() => WriteStateAsync(CancellationToken.None);

        public async Task WriteStateAsync(CancellationToken cancellationToken)
        {
            Assert.Equal(CancellationToken.None, cancellationToken);
            var snapshot = new HashSet<QualifiedStreamId>(State.Streams);
            WriteAttempts.Add(new(snapshot));
            if (BeforeWrite is { } beforeWrite)
            {
                await beforeWrite();
            }

            var failure = _writeFailure;
            _writeFailure = null;
            if (failure is null || _commitBeforeThrow)
            {
                store.Streams = new(snapshot);
                RecordExists = true;
            }

            if (failure is not null)
            {
                throw failure;
            }
        }

        public Task ClearStateAsync() => throw new NotSupportedException("Registry must retire individual durable intents.");
    }
}
