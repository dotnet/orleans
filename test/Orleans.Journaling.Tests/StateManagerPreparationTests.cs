using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Xunit;

namespace Orleans.Journaling.Tests;

public partial class StateManagerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preparation_RunsBeforeCaptureAndAcknowledgement(bool snapshot)
    {
        var storage = new CapturingStorage { IsCompactionRequested = snapshot };
        await using var manager = CreateTestSystem(storage).Manager;
        List<string> events = [];
        var state = new PreparingValue("state", manager, CreateValueCodec<int>(), events);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        state["intent"] = 17;

        await manager.WriteStateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["prepare", "capture", "ack"], events);
        Assert.Equal(17, state.CapturedValue);
        Assert.Equal(1, state.WriteCompletedCount);
        Assert.Single(snapshot ? storage.Replaces : storage.Appends);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preparation_LaterAsynchronousStateRechecksEarlierInterleavedChanges(bool snapshot)
    {
        var storage = new CapturingStorage { IsCompactionRequested = snapshot };
        await using var manager = CreateTestSystem(storage).Manager;
        var earlier = new PreparingValue("earlier", manager, CreateValueCodec<int>());
        var later = new PreparingValue("later", manager, CreateValueCodec<int>());
        var entered = NewSignal();
        var release = NewSignal();
        var prepared = false;
        later.Preparation = async token =>
        {
            if (!prepared)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                later.PreparedValue = later.CurrentValue;
                prepared = true;
            }
        };
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        later["intent"] = 23;
        var write = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        try
        {
            await WaitFor(entered.Task);
            Assert.Equal(0, earlier.CaptureCount);
            Assert.Empty(storage.Appends);
            Assert.Empty(storage.Replaces);
            earlier["intent"] = 41;
            release.TrySetResult();
            await WaitFor(write);
            Assert.Equal(41, earlier.CapturedValue);
            Assert.Equal(41, earlier.PreparedValue);
            Assert.Equal(2, earlier.PrepareCalls);
            Assert.Equal(23, later.CapturedValue);
            Assert.Equal(1, earlier.WriteCompletedCount);

            await using var recovered = CreateTestSystem(storage).Manager;
            var replayed = new PreparingValue("earlier", recovered, CreateValueCodec<int>());
            await recovered.InitializeAsync(TestContext.Current.CancellationToken);
            Assert.Equal(41, replayed["intent"]);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preparation_SynchronousReadyPassAndCaptureShareOneTurn(bool snapshot)
    {
        var scheduler = new QueuedTaskScheduler();
        await scheduler.Run(async () =>
        {
            var storage = new CapturingStorage { IsCompactionRequested = snapshot };
            await using var manager = CreateTestSystem(storage).Manager;
            var state = new PreparingValue("state", manager, CreateValueCodec<int>());
            Task? mutation = null;
            state.Preparation = _ =>
            {
                state.PreparedValue = state.CurrentValue;
                mutation ??= scheduler.Queue(() => state["intent"] = 29);
                return ValueTask.CompletedTask;
            };
            await manager.InitializeAsync(TestContext.Current.CancellationToken);
            state["intent"] = 13;

            await manager.WriteStateAsync(TestContext.Current.CancellationToken);
            await mutation!;

            Assert.Equal(13, state.CapturedValue);
            Assert.Equal(29, state.CurrentValue);
            await using var recovered = CreateTestSystem(storage).Manager;
            var replayed = new PreparingValue("state", recovered, CreateValueCodec<int>());
            await recovered.InitializeAsync(TestContext.Current.CancellationToken);
            Assert.Equal(13, replayed["intent"]);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preparation_FailureRetainsPendingStateForExplicitRetry(bool snapshot)
    {
        var storage = new CapturingStorage { IsCompactionRequested = snapshot };
        await using var manager = CreateTestSystem(storage).Manager;
        var failure = new IOException("State prerequisite failed.");
        var state = new PreparingValue("state", manager, CreateValueCodec<int>())
        {
            Preparation = _ => ValueTask.FromException(failure)
        };
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        state["intent"] = 31;

        var caught = await Assert.ThrowsAsync<JournaledStatePreCommitException>(() =>
            manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask());

        Assert.Same(failure, caught.InnerException);
        Assert.Equal(0, state.CaptureCount);
        Assert.Equal(0, state.WriteCompletedCount);
        Assert.Empty(storage.Appends);
        Assert.Empty(storage.Replaces);
        state.Preparation = null;
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(31, state.CapturedValue);
        Assert.Single(snapshot ? storage.Replaces : storage.Appends);
        Assert.Empty(snapshot ? storage.Appends : storage.Replaces);
    }

    [Fact]
    public async Task Preparation_CallerCancellationLeavesOwnedPreparationAndWriteRunning()
    {
        var storage = new CapturingStorage();
        await using var manager = CreateTestSystem(storage).Manager;
        var state = new PreparingValue("state", manager, CreateValueCodec<int>());
        var entered = NewSignal();
        var release = NewSignal();
        var prepared = false;
        state.Preparation = async token =>
        {
            if (!prepared)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                state.PreparedValue = state.CurrentValue;
                prepared = true;
            }
        };
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        state["intent"] = 47;
        using var caller = new CancellationTokenSource();
        var write = manager.WriteStateAsync(caller.Token).AsTask();
        try
        {
            await WaitFor(entered.Task);
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WaitFor(write));
            Assert.Equal(0, state.CaptureCount);
            release.TrySetResult();
            await WaitFor(state.Acknowledged.Task);
            Assert.Equal(47, state.CapturedValue);
            Assert.Single(storage.Appends);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task Preparation_ShutdownDrainsPreparationAndPreventsCapture()
    {
        var storage = new CapturingStorage();
        var manager = CreateTestSystem(storage).Manager;
        var state = new PreparingValue("state", manager, CreateValueCodec<int>());
        var entered = NewSignal();
        var release = NewSignal();
        var observedCancellation = false;
        state.Preparation = async token =>
        {
            entered.TrySetResult();
            await release.Task;
            observedCancellation = token.IsCancellationRequested;
            state.PreparedValue = state.CurrentValue;
        };
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        state["intent"] = 53;
        var write = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        try
        {
            await WaitFor(entered.Task);
            var disposal = manager.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            release.TrySetResult();
            await WaitFor(disposal);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WaitFor(write));
            Assert.True(observedCancellation);
            Assert.Equal(0, state.CaptureCount);
            Assert.Equal(0, state.WriteCompletedCount);
            Assert.Empty(storage.Appends);
        }
        finally
        {
            release.TrySetResult();
            await manager.DisposeAsync();
        }
    }

    [Fact]
    public void PreCommitFailure_RoundTripsWithOriginalCause()
    {
        Exception original = new JournaledStatePreCommitException(new IOException("Scheduling failed."));
        var serializer = ServiceProvider.GetRequiredService<Serializer>();

        var copy = Assert.IsType<JournaledStatePreCommitException>(serializer.Deserialize<Exception>(serializer.SerializeToArray(original)));

        Assert.Equal(original.Message, copy.Message);
        Assert.Equal("Scheduling failed.", Assert.IsType<IOException>(copy.InnerException).Message);
    }

    private sealed class QueuedTaskScheduler : TaskScheduler
    {
        private readonly Channel<Task> _queue = Channel.CreateUnbounded<Task>();

        public override int MaximumConcurrencyLevel => 1;
        protected override IEnumerable<Task>? GetScheduledTasks() => null;
        protected override void QueueTask(Task task) => Assert.True(_queue.Writer.TryWrite(task));
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        public Task Queue(Action action) =>
            Task.Factory.StartNew(action, TestContext.Current.CancellationToken, TaskCreationOptions.DenyChildAttach, this);

        public async Task Run(Func<Task> action)
        {
            var task = Task.Factory.StartNew(action, TestContext.Current.CancellationToken, TaskCreationOptions.DenyChildAttach, this).Unwrap();
            while (!task.IsCompleted)
            {
                var next = await _queue.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                var previous = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(null);
                try
                {
                    Assert.True(TryExecuteTask(next));
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(previous);
                }
            }

            await task;
        }
    }
    private sealed class PreparingValue : IStateMachine, IDurableValueCommandHandler<int>
    {
        private readonly IDurableValueCommandCodec<int> _codec;
        private readonly List<string>? _events;
        private JournalStreamWriter _writer;

        public PreparingValue(string name, IJournaledStateManager manager, IDurableValueCommandCodec<int> codec, List<string>? events = null)
        {
            _codec = codec;
            _events = events;
            manager.RegisterStateMachine(name, this);
        }
        public Func<CancellationToken, ValueTask>? Preparation { get; set; }
        public int PrepareCalls { get; private set; }
        public int PreparedValue { get; set; }
        public int CapturedValue { get; private set; }
        public int CaptureCount { get; private set; }
        public int WriteCompletedCount { get; private set; }
        private int _value;
        public int CurrentValue => _value;
        public int this[string key]
        {
            get => _value;
            set { _codec.WriteSet(value, _writer); _value = value; }
        }
        void IDurableValueCommandHandler<int>.ApplySet(int command) => _value = command;
        public void ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
            context.GetRequiredCommandCodec(entry.FormatKey, _codec).Apply(entry.Reader, this);
        public void Reset(JournalStreamWriter writer) { _writer = writer; _value = 0; }
        public void WritePendingEntries(JournalStreamWriter writer) => Capture();
        public void WriteSnapshot(JournalStreamWriter writer) { _codec.WriteSet(_value, writer); Capture(); }
        public TaskCompletionSource Acknowledged { get; } = NewSignal();

        public ValueTask PrepareWriteAsync(CancellationToken cancellationToken = default)
        {
            PrepareCalls++;
            _events?.Add("prepare");
            if (Preparation is { } preparation) return preparation(cancellationToken);
            PreparedValue = CurrentValue;
            return ValueTask.CompletedTask;
        }

        private void Capture()
        {
            _events?.Add("capture");
            CaptureCount++;
            CapturedValue = CurrentValue;
            Assert.Equal(PreparedValue, CapturedValue);
        }

        public void OnWriteCompleted()
        {
            _events?.Add("ack");
            WriteCompletedCount++;
            Acknowledged.TrySetResult();
        }
    }
}
