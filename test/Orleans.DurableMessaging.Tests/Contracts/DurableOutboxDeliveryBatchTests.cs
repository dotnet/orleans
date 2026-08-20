using System.Collections;
using System.Diagnostics.CodeAnalysis;
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

[TestCategory("BVT"), TestCategory("Journaling")]
public sealed class DurableOutboxDeliveryBatchTests
{
    [Fact]
    public async Task CancellationAfterSuccessfulDeliveryRevertsRemoval()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new OutboxFixture(
            _ =>
            {
                cancellation.Cancel();
                return ValueTask.FromResult(DeliveryResult.Accepted());
            });

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.DeliverAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(fixture.Messages.ContainsKey(fixture.MessageId));
        Assert.True(fixture.MessageStates.ContainsKey(fixture.MessageId));
        Assert.Equal(0, fixture.DeadLetters.Count);
        Assert.Equal(0, fixture.Manager.WriteCount);
        Assert.Equal(1, fixture.Manager.RevertCount);
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

        var exception = await Assert.ThrowsAsync<IOException>(
            () => fixture.DeliverAsync());

        Assert.Same(writeFailure, exception);
        Assert.True(fixture.Messages.ContainsKey(fixture.MessageId));
        Assert.True(fixture.MessageStates.ContainsKey(fixture.MessageId));
        Assert.Equal(0, fixture.DeadLetters.Count);
        Assert.Equal(1, fixture.Manager.RevertCount);
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
        private readonly object _outbox;
        private readonly MethodInfo _deliverMethod;

        public OutboxFixture(
            Func<CancellationToken, ValueTask<DeliveryResult>> deliver,
            int maxDeliveryAttempts = 3,
            Exception? writeException = null,
            Exception? revertException = null)
        {
            MessageId = Guid.NewGuid();
            var senderId = GrainId.Create("sender", "1");
            var receiverId = GrainId.Create("receiver", "1");
            var envelope = new DurableEnvelope
            {
                MessageId = MessageId,
                SenderId = senderId,
                ReceiverId = receiverId,
                RouteKey = "test",
                Data = (DurableEnvelopeData)RuntimeHelpers.GetUninitializedObject(typeof(DurableEnvelopeData))
            };

            Messages.Add(MessageId, envelope);
            MessageStates = CreateInternalDictionary("Orleans.DurableMessaging.OutboxMessageState");
            DeadLetters = CreateInternalDictionary("Orleans.DurableMessaging.OutboxDeadLetter");
            var messageState = CreateInternal("Orleans.DurableMessaging.OutboxMessageState");
            messageState.GetType().GetProperty("EnqueuedAt")!.SetValue(messageState, TimeProvider.System.GetUtcNow());
            MessageStates.Add(MessageId, messageState);
            Manager = new TestStateManager(
                [Messages, MessageStates, DeadLetters],
                writeException,
                revertException);

            var inbox = Substitute.For<IDurableInboxExtension>();
            inbox.DeliverAsync(Arg.Any<DurableEnvelope>(), Arg.Any<CancellationToken>())
                .Returns(call => deliver(call.ArgAt<CancellationToken>(1)));
            var grainFactory = Substitute.For<IGrainFactory>();
            grainFactory.GetGrain<IDurableInboxExtension>(Arg.Any<GrainId>()).Returns(inbox);
            var grainContext = Substitute.For<IGrainContext>();
            grainContext.GrainId.Returns(senderId);
            grainContext.ObservableLifecycle.Returns(Substitute.For<IGrainLifecycle>());

            var outboxType = GetInternalType("Orleans.DurableMessaging.DurableOutbox");
            var instrumentsType = GetInternalType("Orleans.DurableMessaging.DurableMessagingInstruments");
            var instruments = instrumentsType
                .GetMethod("CreateForDirectConstruction", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, null)!;
            var pumpResults = Activator.CreateInstance(
                GetInternalType("Orleans.DurableMessaging.DurableMessagingPumpResults"),
                nonPublic: true)!;
            var logger = Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(outboxType))!;

            _outbox = Activator.CreateInstance(
                outboxType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                [
                    Manager,
                    Messages,
                    grainFactory,
                    grainContext,
                    Substitute.For<ITimerRegistry>(),
                    logger,
                    instruments,
                    MessageStates.Instance,
                    DeadLetters.Instance,
                    new TestDurableValue<string>(),
                    new TestDurableValue<string>(),
                    new TestDurableValue<long>(),
                    Substitute.For<ILocalDurableJobManager>(),
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
        }

        public Guid MessageId { get; }
        public TestDurableDictionary<Guid, DurableEnvelope> Messages { get; } = new();
        public UntypedDurableDictionary MessageStates { get; }
        public UntypedDurableDictionary DeadLetters { get; }
        public TestStateManager Manager { get; }

        public Task DeliverAsync(CancellationToken cancellationToken = default) =>
            (Task)_deliverMethod.Invoke(_outbox, [cancellationToken])!;

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
        object Capture();
        void Restore(object snapshot);
    }

    private sealed class UntypedDurableDictionary(object instance) : ITestDurableState
    {
        private readonly Type _type = instance.GetType();

        public object Instance { get; } = instance;
        public int Count => (int)_type.GetProperty("Count")!.GetValue(Instance)!;

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
        Exception? revertException) : IJournaledStateManager
    {
        private readonly ITestDurableState[] _states = states.ToArray();
        private object[] _durableSnapshots = states.Select(static state => state.Capture()).ToArray();
        private IJournaledStateObserver? _observer;

        public bool SupportsRollback => true;
        public int WriteCount { get; private set; }
        public int RevertCount { get; private set; }

        public ValueTask InitializeAsync(CancellationToken cancellationToken) => default;
        public void RegisterState(string name, IJournaledState state) { }
        public void RegisterObserver(IJournaledStateObserver observer) => _observer = observer;

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
            _durableSnapshots = _states.Select(static state => state.Capture()).ToArray();
            _observer?.OnWriteCompleted();
            return default;
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

            _observer?.OnRecoveryStarted();
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

        public TValue this[TKey key] { get => _items[key]; set => _items[key] = value; }
        public ICollection<TKey> Keys => _items.Keys;
        public ICollection<TValue> Values => _items.Values;
        public int Count => _items.Count;
        public bool IsReadOnly => false;

        public void Add(TKey key, TValue value) => _items.Add(key, value);
        public void Add(KeyValuePair<TKey, TValue> item) => ((ICollection<KeyValuePair<TKey, TValue>>)_items).Add(item);
        public void Clear() => _items.Clear();
        public bool Contains(KeyValuePair<TKey, TValue> item) => ((ICollection<KeyValuePair<TKey, TValue>>)_items).Contains(item);
        public bool ContainsKey(TKey key) => _items.ContainsKey(key);
        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex) =>
            ((ICollection<KeyValuePair<TKey, TValue>>)_items).CopyTo(array, arrayIndex);
        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _items.GetEnumerator();
        public bool Remove(TKey key) => _items.Remove(key);
        public bool Remove(KeyValuePair<TKey, TValue> item) =>
            ((ICollection<KeyValuePair<TKey, TValue>>)_items).Remove(item);
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
