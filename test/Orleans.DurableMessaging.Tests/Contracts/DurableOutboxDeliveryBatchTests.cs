using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.DurableJobs;
using Orleans.DurableMessaging.Configuration;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Timers;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Contracts;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class DurableOutboxDeliveryBatchTests
{
    [Fact]
    public async Task DuplicateAfterCommitRemainsDeliverableAndUnfenced()
    {
        var fixture = new OutboxFixture(hasDurableMessage: false);
        fixture.Send(fixture.Envelope);
        await fixture.CommitAsync();
        var duplicate = fixture.CreateEquivalentEnvelope();

        fixture.Send(duplicate);

        Assert.NotSame(fixture.Envelope.Data, duplicate.Data);
        Assert.Equal(0, fixture.PendingMessageCount);
        await fixture.DeliverAsync();
        Assert.Equal(1, fixture.DeliveryCount);
        Assert.False(fixture.Messages.ContainsKey(fixture.MessageId));
    }

    [Fact]
    public void SenderMustMatchOwningGrain()
    {
        var fixture = new OutboxFixture(hasDurableMessage: false);
        var envelope = fixture.CreateEnvelope(
            Guid.NewGuid(),
            senderId: GrainId.Create("sender", "spoofed"));

        var exception = Assert.Throws<InvalidOperationException>(() => fixture.Send(envelope));

        Assert.Contains("does not match the owning grain", exception.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Messages);
    }

    [Fact]
    public async Task DurableDuplicateFollowedByNoOpWriteDoesNotFenceDelivery()
    {
        var fixture = new OutboxFixture();

        fixture.Send(fixture.CreateEquivalentEnvelope());
        await fixture.CommitAsync();

        Assert.Equal(0, fixture.Manager.WriteCompletedCount);
        Assert.Equal(0, fixture.PendingMessageCount);
        await fixture.DeliverAsync();
        Assert.Equal(1, fixture.DeliveryCount);
    }

    [Fact]
    public async Task DuplicateBeforeFirstCommitRemainsPendingUntilOwningCommit()
    {
        var fixture = new OutboxFixture(hasDurableMessage: false);

        fixture.Send(fixture.Envelope);
        fixture.Send(fixture.CreateEquivalentEnvelope());

        Assert.Single(fixture.Messages);
        Assert.Equal(1, fixture.PendingMessageCount);
        await fixture.DeliverAsync();
        Assert.Equal(0, fixture.DeliveryCount);

        await fixture.CommitAsync();

        Assert.Equal(0, fixture.PendingMessageCount);
        await fixture.DeliverAsync();
        Assert.Equal(1, fixture.DeliveryCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConflictingDuplicateFailsWithoutMutatingDurableOrProvisionalMessage(bool commitFirst)
    {
        var fixture = new OutboxFixture(hasDurableMessage: false);
        fixture.Send(fixture.Envelope);
        if (commitFirst)
        {
            await fixture.CommitAsync();
        }

        var exception = Assert.Throws<InvalidOperationException>(
            () => fixture.Send(fixture.CreateConflictingEnvelope()));

        Assert.Contains(fixture.MessageId.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.True(fixture.Messages.TryGetValue(fixture.MessageId, out var stored));
        Assert.Equal(fixture.Envelope.RouteKey, stored.RouteKey);
        Assert.Equal(commitFirst ? 0 : 1, fixture.PendingMessageCount);
    }

    [Fact]
    public async Task RollbackClearsFenceAndAllowsMessageToBeSentAgain()
    {
        var fixture = new OutboxFixture(hasDurableMessage: false);
        fixture.Send(fixture.Envelope);

        await fixture.Manager.RevertPendingChangesAsync(CancellationToken.None);

        Assert.Empty(fixture.Messages);
        Assert.Equal(0, fixture.PendingMessageCount);
        fixture.Send(fixture.CreateEquivalentEnvelope());
        Assert.Equal(1, fixture.PendingMessageCount);

        await fixture.CommitAsync();
        Assert.Equal(0, fixture.PendingMessageCount);
        await fixture.DeliverAsync();
        Assert.Equal(1, fixture.DeliveryCount);
    }

    [Fact]
    public async Task MessageAddedAfterWriteCaptureRemainsFencedForNextCommit()
    {
        var fixture = new OutboxFixture(hasDurableMessage: false, runTimersImmediately: true);
        fixture.Send(fixture.Envelope);
        var reentrantEnvelope = fixture.CreateEnvelope(Guid.NewGuid());

        fixture.Manager.CommitWithInterleavedMutation(() => fixture.Send(reentrantEnvelope));

        Assert.False(fixture.IsPending(fixture.MessageId));
        Assert.True(fixture.IsPending(reentrantEnvelope.MessageId));
        await fixture.WaitForEnsureJobTimersAsync();
        Assert.Equal(1, fixture.ScheduledJobCount);
        await fixture.CommitAsync();
        Assert.Equal(0, fixture.PendingMessageCount);
    }

    [Fact]
    public async Task CancellationAfterSuccessfulDeliveryRevertsRemoval()
    {
        CancellationTokenSource? cancellation = null;
        var fixture = new OutboxFixture(
            _ =>
            {
                cancellation!.Cancel();
                return ValueTask.FromResult(DeliveryResult.Accepted());
            });
        fixture.ActivateMetrics();
        Assert.Equal(1, fixture.GetOutboxDepth());

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var currentCancellation = new CancellationTokenSource();
            cancellation = currentCancellation;
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => fixture.DeliverAsync(currentCancellation.Token));

            Assert.Equal(currentCancellation.Token, exception.CancellationToken);
            Assert.True(fixture.Messages.ContainsKey(fixture.MessageId));
            Assert.True(fixture.MessageStates.ContainsKey(fixture.MessageId));
            Assert.Equal(0, fixture.DeadLetters.Count);
            Assert.Equal(1, fixture.GetOutboxDepth());
        }

        Assert.Equal(0, fixture.Manager.WriteCount);
        Assert.Equal(2, fixture.Manager.RevertCount);
    }

    [Fact]
    public async Task CancellationAfterDeadLetterMutationRevertsFailureState()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new OutboxFixture(
            _ =>
            {
                cancellation.Cancel();
                return ValueTask.FromResult(DeliveryResult.RouteNotFound("missing"));
            },
            maxDeliveryAttempts: 1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.DeliverAsync(cancellation.Token));

        Assert.True(fixture.Messages.ContainsKey(fixture.MessageId));
        Assert.Equal(0, fixture.MessageStates.GetProperty<int>(fixture.MessageId, "AttemptCount"));
        Assert.Equal(0, fixture.DeadLetters.Count);
        Assert.Equal(0, fixture.Manager.WriteCount);
        Assert.Equal(1, fixture.Manager.RevertCount);
    }

    [Fact]
    public async Task LaterStateWriteDoesNotCommitRevertedDeliveryMutation()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new OutboxFixture(
            _ =>
            {
                cancellation.Cancel();
                return ValueTask.FromResult(DeliveryResult.Accepted());
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.DeliverAsync(cancellation.Token));
        await fixture.Manager.WriteStateAsync(CancellationToken.None);
        await fixture.Manager.RevertPendingChangesAsync(CancellationToken.None);

        Assert.True(fixture.Messages.ContainsKey(fixture.MessageId));
        Assert.True(fixture.MessageStates.ContainsKey(fixture.MessageId));
        Assert.Equal(0, fixture.DeadLetters.Count);
    }

    [Fact]
    public async Task RevertFailureIsSurfaced()
    {
        var writeFailure = new IOException("Injected delivery batch write failure.");
        var revertFailure = new InvalidOperationException("Injected fenced recovery failure.");
        var fixture = new OutboxFixture(
            _ => ValueTask.FromResult(DeliveryResult.Accepted()),
            writeException: writeFailure,
            revertException: revertFailure);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.DeliverAsync());

        Assert.Same(revertFailure, exception);
        Assert.Equal(1, fixture.Manager.WriteCount);
        Assert.Equal(1, fixture.Manager.RevertCount);
    }

    [Fact]
    public async Task WriteFailureRevertsProvisionalMutationsAndPreservesError()
    {
        var writeFailure = new IOException("Injected delivery batch write failure.");
        var fixture = new OutboxFixture(
            _ => ValueTask.FromResult(DeliveryResult.Accepted()),
            writeException: writeFailure);
        fixture.ActivateMetrics();
        Assert.Equal(1, fixture.GetOutboxDepth());

        var exception = await Assert.ThrowsAsync<IOException>(
            () => fixture.DeliverAsync());

        Assert.Same(writeFailure, exception);
        Assert.True(fixture.Messages.ContainsKey(fixture.MessageId));
        Assert.True(fixture.MessageStates.ContainsKey(fixture.MessageId));
        Assert.Equal(0, fixture.DeadLetters.Count);
        Assert.Equal(1, fixture.Manager.RevertCount);
        Assert.Equal(1, fixture.GetOutboxDepth());
    }

    [Fact]
    public void ConstructionWithoutObserverSupport_FailsWithSpecificDiagnostic()
    {
        var exception = Assert.Throws<TargetInvocationException>(
            () => new OutboxFixture(supportsObservers: false));

        var diagnostic = Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Contains("Durable messaging", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("IJournaledStateManager.RegisterObserver", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NormalDeliveryCommitsBatchOnce()
    {
        var fixture = new OutboxFixture(
            _ => ValueTask.FromResult(DeliveryResult.Accepted()));

        await fixture.DeliverAsync();

        Assert.False(fixture.Messages.ContainsKey(fixture.MessageId));
        Assert.False(fixture.MessageStates.ContainsKey(fixture.MessageId));
        Assert.Equal(1, fixture.Manager.WriteCount);
        Assert.Equal(0, fixture.Manager.RevertCount);
    }

    [Fact]
    public async Task CancellationBeforeMutationDoesNotRevert()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new OutboxFixture(
            token =>
            {
                cancellation.Cancel();
                return ValueTask.FromException<DeliveryResult>(new OperationCanceledException(token));
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.DeliverAsync(cancellation.Token));

        Assert.True(fixture.Messages.ContainsKey(fixture.MessageId));
        Assert.Equal(0, fixture.Manager.WriteCount);
        Assert.Equal(0, fixture.Manager.RevertCount);
    }

    private sealed class OutboxFixture
    {
        private static readonly Assembly DurableMessagingAssembly = typeof(IDurableOutbox).Assembly;
        private readonly IDurableOutbox _outbox;
        private readonly MethodInfo _deliverMethod;
        private readonly Instrument _outboxDepthInstrument;
        private readonly FieldInfo _pendingMessageIdsField;
        private readonly RecordingJobManager _jobManager = new();
        private readonly ImmediateTimerRegistry? _immediateTimerRegistry;
        private readonly ITimerRegistry _timerRegistry;

        public OutboxFixture(
            Func<CancellationToken, ValueTask<DeliveryResult>>? deliver = null,
            int maxDeliveryAttempts = 3,
            Exception? writeException = null,
            Exception? revertException = null,
            bool hasDurableMessage = true,
            bool supportsObservers = true,
            bool runTimersImmediately = false)
        {
            _timerRegistry = runTimersImmediately
                ? _immediateTimerRegistry = new ImmediateTimerRegistry()
                : Substitute.For<ITimerRegistry>();
            MessageId = Guid.NewGuid();
            SenderId = GrainId.Create("sender", "1");
            ReceiverId = GrainId.Create("receiver", "1");
            Envelope = CreateEnvelope(MessageId);

            MessageStates = CreateInternalDictionary("Orleans.DurableMessaging.OutboxMessageState");
            DeadLetters = CreateInternalDictionary("Orleans.DurableMessaging.OutboxDeadLetter");
            if (hasDurableMessage)
            {
                Messages.Add(MessageId, Envelope);
                var messageState = CreateInternal("Orleans.DurableMessaging.OutboxMessageState");
                messageState.GetType().GetProperty("EnqueuedAt")!.SetValue(messageState, TimeProvider.System.GetUtcNow());
                MessageStates.Add(MessageId, messageState);
            }

            Manager = new TestStateManager(
                [Messages, MessageStates, DeadLetters],
                writeException,
                revertException,
                supportsObservers);

            var delivery = deliver ?? (_ => ValueTask.FromResult(DeliveryResult.Accepted()));
            var inbox = new TestInboxExtension(
                (_, cancellationToken) =>
                {
                    DeliveryCount++;
                    return delivery(cancellationToken);
                });
            var grainFactory = new TestGrainFactory(inbox);
            var grainContext = Substitute.For<IGrainContext>();
            grainContext.GrainId.Returns(SenderId);
            grainContext.ObservableLifecycle.Returns(Substitute.For<IGrainLifecycle>());

            var outboxType = GetInternalType("Orleans.DurableMessaging.DurableOutbox");
            var instrumentsType = GetInternalType("Orleans.DurableMessaging.DurableMessagingInstruments");
            var instruments = instrumentsType
                .GetMethod("CreateForDirectConstruction", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, null)!;
            var depthTracker = instrumentsType
                .GetField("_outboxDepth", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(instruments)!;
            _outboxDepthInstrument = (Instrument)depthTracker.GetType()
                .GetField("_gauge", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(depthTracker)!;
            var pumpResults = Activator.CreateInstance(
                GetInternalType("Orleans.DurableMessaging.DurableMessagingPumpResults"),
                nonPublic: true)!;
            var logger = Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(outboxType))!;

            _outbox = (IDurableOutbox)Activator.CreateInstance(
                outboxType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                [
                    Manager,
                    Messages,
                    grainFactory,
                    grainContext,
                    _timerRegistry,
                    logger,
                    instruments,
                    MessageStates.Instance,
                    DeadLetters.Instance,
                    new TestDurableValue<string>(),
                    new TestDurableValue<string>(),
                    new TestDurableValue<long>(),
                    _jobManager,
                    Substitute.For<IDurableJobHandlerRegistry>(),
                    pumpResults,
                    TimeProvider.System,
                    Options.Create(
                        new DurableInboxOptions
                        {
                            BackpressureRetryDelay = TimeSpan.FromMilliseconds(1),
                            MaxOutboxRetryAge = TimeSpan.FromMinutes(5),
                            MaxDeliveryAttempts = maxDeliveryAttempts,
                            OutboxBatchSize = 8
                        })
                ],
                culture: null)!;
            _deliverMethod = outboxType.GetMethod("DeliverPendingMessagesAsync")!;
            _pendingMessageIdsField = outboxType.GetField("_pendingMessageIds", BindingFlags.Instance | BindingFlags.NonPublic)!;
        }

        public Guid MessageId { get; }
        public GrainId SenderId { get; }
        public GrainId ReceiverId { get; }
        public DurableEnvelope Envelope { get; }
        public int DeliveryCount { get; private set; }
        public TestDurableDictionary<Guid, DurableEnvelope> Messages { get; } = new();
        public UntypedDurableDictionary MessageStates { get; }
        public UntypedDurableDictionary DeadLetters { get; }
        public TestStateManager Manager { get; }
        public int PendingMessageCount => GetPendingMessageIds().Count;
        public int ScheduledJobCount => _jobManager.ScheduleCount;

        public Task DeliverAsync(CancellationToken cancellationToken = default) =>
            (Task)_deliverMethod.Invoke(_outbox, [cancellationToken])!;

        public void ActivateMetrics() =>
            _outbox.GetType()
                .GetMethod("EnsureMetricsActive", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(_outbox, null);

        public long GetOutboxDepth()
        {
            long? result = null;
            using var listener = new MeterListener
            {
                InstrumentPublished = (instrument, meterListener) =>
                {
                    if (ReferenceEquals(instrument, _outboxDepthInstrument))
                    {
                        meterListener.EnableMeasurementEvents(instrument);
                    }
                }
            };
            listener.SetMeasurementEventCallback<long>(
                (instrument, measurement, tags, state) => result = measurement);
            listener.Start();
            listener.RecordObservableInstruments();
            return result ?? throw new InvalidOperationException("The outbox depth gauge did not report a value.");
        }

        public void Send(DurableEnvelope envelope) => _outbox.Send(envelope);

        public ValueTask CommitAsync() => Manager.WriteStateAsync(CancellationToken.None);

        public Task WaitForEnsureJobTimersAsync() =>
            _immediateTimerRegistry?.WaitForCallbacksAsync() ?? Task.CompletedTask;

        public bool IsPending(Guid messageId) => GetPendingMessageIds().Contains(messageId);

        public DurableEnvelope CreateEquivalentEnvelope() => CreateEnvelope(MessageId);

        public DurableEnvelope CreateConflictingEnvelope() => CreateEnvelope(MessageId, routeKey: "conflict");

        public DurableEnvelope CreateEnvelope(
            Guid messageId,
            string routeKey = "test",
            GrainId? senderId = null) => new()
        {
            MessageId = messageId,
            SenderId = senderId ?? SenderId,
            ReceiverId = ReceiverId,
            RouteKey = routeKey,
            CorrelationKey = HierarchicalKey.Create("operation/1"),
            ReplyTo = SenderId,
            Data = CreateEnvelopeData(),
            CreatedAt = DateTimeOffset.UnixEpoch
        };

        private HashSet<Guid> GetPendingMessageIds() =>
            (HashSet<Guid>)_pendingMessageIdsField.GetValue(_outbox)!;

        private sealed class TestInboxExtension(
            Func<DurableEnvelope, CancellationToken, ValueTask<DeliveryResult>> deliver) : IDurableInboxExtension
        {
            public ValueTask<DeliveryResult> DeliverAsync(
                DurableEnvelope envelope,
                CancellationToken cancellationToken = default) =>
                deliver(envelope, cancellationToken);
        }

        private sealed class RecordingJobManager : ILocalDurableJobManager
        {
            private int _scheduleCount;

            public int ScheduleCount => Volatile.Read(ref _scheduleCount);

            public Task<DurableJob> ScheduleJobAsync(
                ScheduleJobRequest request,
                CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _scheduleCount);
                return Task.FromResult(new DurableJob
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = request.JobName,
                    DueTime = request.DueTime,
                    TargetGrainId = request.Target,
                    Metadata = request.Metadata,
                    ShardId = "test",
                });
            }

            public Task<bool> TryCancelDurableJobAsync(
                DurableJob job,
                CancellationToken cancellationToken) => Task.FromResult(true);
        }

        private sealed class ImmediateTimerRegistry : ITimerRegistry
        {
            private readonly List<Task> _callbacks = [];

            [Obsolete]
            public IDisposable RegisterTimer(
                IGrainContext grainContext,
                Func<object?, Task> callback,
                object? state,
                TimeSpan dueTime,
                TimeSpan period) => throw new NotSupportedException();

            public IGrainTimer RegisterGrainTimer<TState>(
                IGrainContext grainContext,
                Func<TState, CancellationToken, Task> callback,
                TState state,
                GrainTimerCreationOptions options)
            {
                _callbacks.Add(callback(state, CancellationToken.None));
                return Substitute.For<IGrainTimer>();
            }

            public Task WaitForCallbacksAsync() => Task.WhenAll(_callbacks);
        }

        private sealed class TestGrainFactory(IDurableInboxExtension inbox) : IGrainFactory
        {
            public TGrainInterface GetGrain<TGrainInterface>(GrainId grainId)
                where TGrainInterface : IAddressable =>
                typeof(TGrainInterface) == typeof(IDurableInboxExtension)
                    ? (TGrainInterface)(object)inbox
                    : throw new NotSupportedException();

            public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix = null)
                where TGrainInterface : IGrainWithGuidKey => throw new NotSupportedException();
            public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix = null)
                where TGrainInterface : IGrainWithIntegerKey => throw new NotSupportedException();
            public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix = null)
                where TGrainInterface : IGrainWithStringKey => throw new NotSupportedException();
            public TGrainInterface GetGrain<TGrainInterface>(
                Guid primaryKey,
                string keyExtension,
                string? grainClassNamePrefix = null)
                where TGrainInterface : IGrainWithGuidCompoundKey => throw new NotSupportedException();
            public TGrainInterface GetGrain<TGrainInterface>(
                long primaryKey,
                string keyExtension,
                string? grainClassNamePrefix = null)
                where TGrainInterface : IGrainWithIntegerCompoundKey => throw new NotSupportedException();
            public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
                where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
            public void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj)
                where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
            public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey) => throw new NotSupportedException();
            public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey) => throw new NotSupportedException();
            public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey) => throw new NotSupportedException();
            public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension) =>
                throw new NotSupportedException();
            public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension) =>
                throw new NotSupportedException();
            public IAddressable GetGrain(GrainId grainId) => throw new NotSupportedException();
            public IAddressable GetGrain(GrainId grainId, GrainInterfaceType interfaceType) =>
                throw new NotSupportedException();
            public IAddressable GetGrain(Type interfaceType, IdSpan grainKey, string grainClassNamePrefix) =>
                throw new NotSupportedException();
            public IAddressable GetGrain(Type interfaceType, IdSpan grainKey) => throw new NotSupportedException();
        }

        private static DurableEnvelopeData CreateEnvelopeData()
        {
            var result = (DurableEnvelopeData)RuntimeHelpers.GetUninitializedObject(typeof(DurableEnvelopeData));
            typeof(DurableEnvelopeData)
                .GetMethod("Initialize", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(
                    result,
                    [
                        new byte[] { 1, 2 },
                        (Offset: 0, Length: 1),
                        new Dictionary<string, (int Offset, int Length)>
                        {
                            ["trace"] = (1, 1)
                        }
                    ]);
            return result;
        }

        private static UntypedDurableDictionary CreateInternalDictionary(string valueTypeName)
        {
            var dictionary = Activator.CreateInstance(
                typeof(TestDurableDictionary<,>).MakeGenericType(typeof(Guid), GetInternalType(valueTypeName)))!;
            return new UntypedDurableDictionary(dictionary);
        }

        private static Type GetInternalType(string typeName) =>
            DurableMessagingAssembly.GetType(typeName, throwOnError: true)!;

        private static object CreateInternal(string typeName) =>
            Activator.CreateInstance(GetInternalType(typeName), nonPublic: true)!;
    }

    private interface ITestDurableState
    {
        long Version { get; }
        object Capture();
        void Restore(object snapshot);
    }

    private sealed class UntypedDurableDictionary(object instance) : ITestDurableState
    {
        private readonly Type _type = instance.GetType();

        public object Instance { get; } = instance;
        public int Count => (int)_type.GetProperty("Count")!.GetValue(Instance)!;
        public long Version => ((ITestDurableState)Instance).Version;

        public void Add(Guid key, object value) =>
            _type.GetMethod("Add", [typeof(Guid), value.GetType()])!.Invoke(Instance, [key, value]);

        public bool ContainsKey(Guid key) =>
            (bool)_type.GetMethod("ContainsKey")!.Invoke(Instance, [key])!;

        public T GetProperty<T>(Guid key, string propertyName)
        {
            var value = _type.GetProperty("Item")!.GetValue(Instance, [key])!;
            return (T)value.GetType().GetProperty(propertyName)!.GetValue(value)!;
        }

        public object Capture() => ((ITestDurableState)Instance).Capture();
        public void Restore(object snapshot) => ((ITestDurableState)Instance).Restore(snapshot);
    }

    private sealed class TestStateManager(
        IEnumerable<ITestDurableState> states,
        Exception? writeException,
        Exception? revertException,
        bool supportsObservers) : IJournaledStateManager
    {
        private readonly ITestDurableState[] _states = states.ToArray();
        private object[] _durableSnapshots = states.Select(static state => state.Capture()).ToArray();
        private long[] _durableVersions = states.Select(static state => state.Version).ToArray();
        private IJournaledStateObserver? _observer;

        public int WriteCount { get; private set; }
        public int WriteCompletedCount { get; private set; }
        public int RevertCount { get; private set; }

        public ValueTask InitializeAsync(CancellationToken cancellationToken) => default;
        public void RegisterState(string name, IJournaledState state) { }
        public void RegisterObserver(IJournaledStateObserver observer)
        {
            if (!supportsObservers)
            {
                throw new NotSupportedException();
            }

            _observer = observer;
        }

        public bool TryGetState(string name, [NotNullWhen(true)] out IJournaledState? state)
        {
            state = null;
            return false;
        }

        public ValueTask WriteStateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteCount++;
            if (writeException is not null)
            {
                return ValueTask.FromException(writeException);
            }

            _observer?.OnWriteStarted();
            var currentVersions = _states.Select(static state => state.Version).ToArray();
            if (currentVersions.SequenceEqual(_durableVersions))
            {
                return default;
            }

            _durableSnapshots = _states.Select(static state => state.Capture()).ToArray();
            _durableVersions = currentVersions;
            _observer?.OnWriteCompleted();
            WriteCompletedCount++;
            return default;
        }

        public void CommitWithInterleavedMutation(Action mutation)
        {
            WriteCount++;
            _observer?.OnWriteStarted();
            var committedSnapshots = _states.Select(static state => state.Capture()).ToArray();
            var committedVersions = _states.Select(static state => state.Version).ToArray();
            mutation();
            _durableSnapshots = committedSnapshots;
            _durableVersions = committedVersions;
            _observer?.OnWriteCompleted();
            WriteCompletedCount++;
        }

        public ValueTask RevertPendingChangesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RevertCount++;
            if (revertException is not null)
            {
                return ValueTask.FromException(revertException);
            }

            for (var i = 0; i < _states.Length; i++)
            {
                _states[i].Restore(_durableSnapshots[i]);
            }

            _durableVersions = _states.Select(static state => state.Version).ToArray();
            _observer?.OnRecoveryCompleted();
            return default;
        }

        public ValueTask DeleteStateAsync(CancellationToken cancellationToken) => default;
    }

    private sealed class TestDurableValue<T> : IDurableValue<T>
    {
        public T? Value { get; set; }
    }

    private sealed class TestDurableDictionary<TKey, TValue>
        : IDurableDictionary<TKey, TValue>, ITestDurableState
        where TKey : notnull
    {
        private readonly Dictionary<TKey, TValue> _items = [];
        private long _version;

        public TValue this[TKey key]
        {
            get => _items[key];
            set
            {
                _items[key] = value;
                _version++;
            }
        }

        public ICollection<TKey> Keys => _items.Keys;
        public ICollection<TValue> Values => _items.Values;
        public int Count => _items.Count;
        public bool IsReadOnly => false;
        public long Version => _version;

        public void Add(TKey key, TValue value)
        {
            _items.Add(key, value);
            _version++;
        }

        public void Add(KeyValuePair<TKey, TValue> item)
        {
            ((ICollection<KeyValuePair<TKey, TValue>>)_items).Add(item);
            _version++;
        }

        public void Clear()
        {
            if (_items.Count > 0)
            {
                _items.Clear();
                _version++;
            }
        }

        public bool Contains(KeyValuePair<TKey, TValue> item) => ((ICollection<KeyValuePair<TKey, TValue>>)_items).Contains(item);
        public bool ContainsKey(TKey key) => _items.ContainsKey(key);
        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex) =>
            ((ICollection<KeyValuePair<TKey, TValue>>)_items).CopyTo(array, arrayIndex);
        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _items.GetEnumerator();
        public bool Remove(TKey key)
        {
            if (!_items.Remove(key))
            {
                return false;
            }

            _version++;
            return true;
        }

        public bool Remove(KeyValuePair<TKey, TValue> item)
        {
            if (!((ICollection<KeyValuePair<TKey, TValue>>)_items).Remove(item))
            {
                return false;
            }

            _version++;
            return true;
        }

        public bool TryGetValue(TKey key, out TValue value) => _items.TryGetValue(key, out value!);
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        object ITestDurableState.Capture() =>
            _items.ToDictionary(static pair => pair.Key, pair => CloneValue(pair.Value));

        void ITestDurableState.Restore(object snapshot)
        {
            _items.Clear();
            foreach (var (key, value) in (Dictionary<TKey, TValue>)snapshot)
            {
                _items.Add(key, CloneValue(value));
            }

            _version++;
        }

        private static TValue CloneValue(TValue value)
        {
            if (value is null || typeof(TValue).IsValueType || value is string)
            {
                return value;
            }

            return (TValue)typeof(object)
                .GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(value, null)!;
        }
    }
}
