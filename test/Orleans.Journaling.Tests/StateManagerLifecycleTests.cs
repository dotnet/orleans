using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Orleans.Journaling.Tests;

public partial class StateManagerTests
{
    [Fact]
    public async Task StateHooks_DefaultsAcceptExistingStates()
    {
        IJournaledState state = new AlwaysWritingState();
        Assert.True(state.IsWritePrepared);
        Assert.True(state.PrepareWriteAsync(CancellationToken.None).IsCompletedSuccessfully);
        state.ValidateWrite();
        state.ValidateDelete();
        state.OnDeleteStarted();
        state.OnFaulted(new IOException("Default notification."));

        var storage = new CapturingStorage();
        await using var manager = CreateTestSystem(storage).Manager;
        manager.RegisterState("state", state);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await manager.DeleteStateAsync(TestContext.Current.CancellationToken);
        Assert.Single(storage.Appends);
        Assert.Equal(1, storage.DeleteCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdmissionVeto_UsesCallerContextAndLeavesManagerHealthy(bool delete)
    {
        var storage = new CapturingStorage();
        await using var manager = CreateTestSystem(storage).Manager;
        var state = new HookState();
        manager.RegisterState("state", state);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        var callerContext = new AsyncLocal<string> { Value = "request" };
        var insideAdmission = true;
        var expected = new InvalidOperationException("Request rejected.");
        var calls = 0;
        void Validate()
        {
            Assert.True(insideAdmission);
            Assert.Equal("request", callerContext.Value);
            calls++;
            throw expected;
        }

        if (delete) state.ValidateDeleteAction = Validate;
        else state.ValidateWriteAction = Validate;
        var request = delete
            ? manager.DeleteStateAsync(TestContext.Current.CancellationToken)
            : manager.WriteStateAsync(TestContext.Current.CancellationToken);
        insideAdmission = false;
        Assert.Equal(1, calls);
        Assert.Same(expected, await Record.ExceptionAsync(() => request.AsTask()));
        Assert.Null(state.Failure);
        Assert.Equal(0, state.DeleteStartedCount);
        Assert.Equal(1, state.ResetCount);
        Assert.Empty(storage.Appends);
        Assert.Equal(0, storage.DeleteCount);

        state.ValidateWriteAction = null;
        state.ValidateDeleteAction = null;
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await manager.DeleteStateAsync(TestContext.Current.CancellationToken);
        Assert.Single(storage.Appends);
        Assert.Equal(1, storage.DeleteCount);
        Assert.Null(state.Failure);
    }

    [Fact]
    public async Task Delete_RechecksAllValidationBeforeAnyStart()
    {
        var storage = new CapturingStorage { BlockNextAppend = true };
        await using var manager = CreateTestSystem(storage).Manager;
        var first = new HookState();
        var second = new HookState();
        manager.RegisterState("first", first);
        manager.RegisterState("second", second);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        var write = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await WaitFor(storage.BlockedAppendStarted.Task);

        var validations = new List<string>();
        first.ValidateDeleteAction = () => validations.Add("first");
        var reject = false;
        var expected = new InvalidOperationException("Deletion became unsafe.");
        second.ValidateDeleteAction = () =>
        {
            validations.Add("second");
            if (reject) throw expected;
        };
        var delete = manager.DeleteStateAsync(TestContext.Current.CancellationToken).AsTask();
        Assert.Equal(["first", "second"], validations);
        reject = true;
        storage.ReleaseAppend.SetResult();
        await WaitFor(write);
        Assert.Same(expected, await Record.ExceptionAsync(() => WaitFor(delete)));
        Assert.Equal(["first", "second", "first", "second"], validations);
        Assert.Equal(0, first.DeleteStartedCount);
        Assert.Equal(0, second.DeleteStartedCount);
        Assert.Equal(0, storage.DeleteCount);
        Assert.Same(expected, first.Failure);
        Assert.Same(expected, second.Failure);
    }

    [Fact]
    public async Task Delete_StartsAfterAllValidationAndResetsBeforeWaiter()
    {
        var storage = new BlockingDeleteStorage();
        await using var manager = CreateTestSystem(storage).Manager;
        var events = new List<string>();
        var first = new HookState();
        var second = new HookState();
        manager.RegisterState("first", first);
        manager.RegisterState("second", second);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        first.ValidateDeleteAction = () => events.Add("validate first");
        second.ValidateDeleteAction = () => events.Add("validate second");
        first.DeleteStartedAction = () => events.Add("start first");
        second.DeleteStartedAction = () => events.Add("start second");
        first.ResetAction = () => events.Add("reset first");
        second.ResetAction = () => events.Add("reset second");

        var delete = manager.DeleteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await WaitFor(storage.FirstDeleteStarted.Task);
        Assert.Equal(
            ["validate first", "validate second", "validate first", "validate second", "start first", "start second"],
            events);
        Assert.Equal(1, first.ResetCount);
        Assert.Equal(1, second.ResetCount);
        Assert.False(delete.IsCompleted);
        storage.AllowFirstDelete.SetResult();
        await WaitFor(delete);
        Assert.Equal(["reset first", "reset second"], events.TakeLast(2));
        Assert.Equal(2, first.ResetCount);
        Assert.Equal(2, second.ResetCount);
    }

    [Theory]
    [InlineData("abc", false)]
    [InlineData("acb", false)]
    [InlineData("bac", false)]
    [InlineData("bca", false)]
    [InlineData("cab", false)]
    [InlineData("cba", false)]
    [InlineData("abc", true)]
    [InlineData("acb", true)]
    [InlineData("bac", true)]
    [InlineData("bca", true)]
    [InlineData("cab", true)]
    [InlineData("cba", true)]
    public async Task Readiness_RechecksAllStatesAfterAwaitBeforeFirstCapture(string order, bool snapshot)
    {
        var storage = new CapturingStorage { IsCompactionRequested = snapshot };
        await using var manager = CreateTestSystem(storage).Manager;
        var entered = NewSignal();
        var release = NewSignal();
        var first = new HookState();
        var blocking = new HookState { Prepared = false };
        var retained = new HookState { Prepared = false };
        var states = new Dictionary<char, HookState> { ['a'] = first, ['b'] = blocking, ['c'] = retained };
        blocking.PrepareAction = async token =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
            blocking.Prepared = true;
        };
        foreach (var key in order)
        {
            var state = states[key];
            manager.RegisterState(key.ToString(), state);
            state.CaptureAction = () => Assert.All(states.Values, value => Assert.True(value.Prepared));
        }

        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        var write = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await WaitFor(entered.Task);
        Assert.All(states.Values, state => Assert.Equal(0, state.CaptureCount));
        first.Prepared = false;
        release.SetResult();
        await WaitFor(write);

        Assert.All(states.Values, state =>
        {
            Assert.Equal(1, state.PrepareCount);
            Assert.Equal(1, state.CaptureCount);
            Assert.Equal(1, state.WriteCompletedCount);
        });
        Assert.Equal(snapshot ? 1 : 0, storage.Replaces.Count);
        Assert.Equal(snapshot ? 0 : 1, storage.Appends.Count);
    }

    [Fact]
    public async Task Readiness_FinalPassAndCaptureShareSchedulerTurn()
    {
        var context = new QueuedSynchronizationContext();
        await context.Run(async () =>
        {
            await using var manager = CreateTestSystem().Manager;
            var state = new HookState { Prepared = false };
            var readyTurn = -1;
            state.PrepareAction = async _ =>
            {
                await Task.Yield();
                state.Prepared = true;
            };
            state.ReadinessAction = () =>
            {
                if (state.Prepared) readyTurn = context.Turn;
                return state.Prepared;
            };
            state.CaptureAction = () => Assert.Equal(readyTurn, context.Turn);
            manager.RegisterState("state", state);
            await manager.InitializeAsync(TestContext.Current.CancellationToken);
            await manager.WriteStateAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, state.PrepareCount);
            Assert.Equal(1, state.CaptureCount);
            Assert.Equal(1, state.WriteCompletedCount);
        });
    }

    [Fact]
    public async Task ZeroByteWrite_PreparesStateWithoutWriteCompleted()
    {
        var storage = new CapturingStorage();
        await using var manager = CreateTestSystem(storage).Manager;
        var state = new HookState { EmitEntry = false };
        manager.RegisterState("state", state);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, manager.PendingWriteByteCount);
        Assert.Equal(1, state.WriteCompletedCount);
        state.Prepared = false;
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, state.PrepareCount);
        Assert.Equal(2, state.CaptureCount);
        Assert.Equal(1, state.WriteCompletedCount);
        Assert.Single(storage.Appends);
    }

    [Theory]
    [InlineData("readiness")]
    [InlineData("prepare")]
    [InlineData("incomplete")]
    public async Task PreparationFailure_FencesBeforeCapture(string failure)
    {
        var storage = new CapturingStorage();
        await using var manager = CreateTestSystem(storage).Manager;
        var expected = new InvalidOperationException("Preparation failed.");
        var state = new HookState { Prepared = false };
        manager.RegisterState("state", state);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        if (failure == "readiness") state.ReadinessAction = () => throw expected;
        else if (failure == "prepare") state.PrepareAction = _ => throw expected;
        else state.PrepareAction = _ => default;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WaitFor(manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask()));
        if (failure == "incomplete") Assert.Contains("without becoming prepared", exception.Message);
        else Assert.Same(expected, exception);
        Assert.Same(exception, state.Failure);
        Assert.Equal(failure == "readiness" ? 0 : 1, state.PrepareCount);
        Assert.Equal(0, state.CaptureCount);
        Assert.Equal(0, state.WriteCompletedCount);
        Assert.Empty(storage.Appends);
        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.Same(exception, rejected.InnerException);
    }

    [Fact]
    public async Task CommittedOnlyWrite_ChecksLatchedStateFailure()
    {
        var storage = new CapturingStorage();
        await using var manager = CreateTestSystem(storage).Manager;
        var state = new HookState();
        manager.RegisterState("state", state);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        var expected = new IOException("Latched state failure.");
        state.ReadinessAction = () => throw expected;
        var write = StartWhileEntryIsOpen();
        Assert.Same(expected, await Record.ExceptionAsync(() => WaitFor(write)));
        Assert.Same(expected, state.Failure);
        Assert.Equal(0, state.CaptureCount);
        Assert.Empty(storage.Appends);

        Task StartWhileEntryIsOpen()
        {
            using var entry = state.Writer.BeginEntry();
            var result = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
            Assert.True(SpinWait.SpinUntil(() => result.IsCompleted, TimeSpan.FromSeconds(10)),
                "The committed-only write must observe readiness while the lexical entry is open.");
            return result;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fault_NotifiesEveryStateBeforeWaitersAndPreservesOriginal(bool failPreparation)
    {
        var expected = new IOException("Original failure.");
        var notificationFailure = new InvalidOperationException("Notification failed.");
        var storage = new CapturingStorage { BlockNextAppend = !failPreparation, NextAppendException = expected };
        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(logger);
        var shared = new JournaledStateManagerShared(new Logger<JournaledStateManager>(loggerFactory),
            Options.Create(ManagerOptions), TimeProvider.System, ServiceProvider);
        await using var manager = new JournaledStateManager(shared, storage);
        var entered = NewSignal();
        var release = NewSignal();
        var first = new HookState { Prepared = !failPreparation };
        var second = new HookState();
        manager.RegisterState("first", first);
        manager.RegisterState("second", second);
        first.PrepareAction = async token =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
            throw expected;
        };
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        var current = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await WaitFor(failPreparation ? entered.Task : storage.BlockedAppendStarted.Task);
        var queued = new[]
        {
            current,
            manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask(),
            manager.DeleteStateAsync(TestContext.Current.CancellationToken).AsTask(),
            manager.InitializeAsync(TestContext.Current.CancellationToken).AsTask()
        };
        var notified = new List<string>();
        first.FaultAction = exception =>
        {
            Assert.Same(expected, exception);
            Assert.All(queued, task => Assert.False(task.IsCompleted));
            notified.Add("first");
            throw notificationFailure;
        };
        second.FaultAction = exception =>
        {
            Assert.Same(expected, exception);
            Assert.All(queued, task => Assert.False(task.IsCompleted));
            var rejected = Assert.Throws<InvalidOperationException>(() => manager.RegisterState("late", new HookState()));
            Assert.Same(expected, rejected.InnerException);
            notified.Add("second");
        };

        if (failPreparation) release.SetResult();
        else storage.ReleaseAppend.SetResult();
        foreach (var task in queued)
        {
            Assert.Same(expected, await Record.ExceptionAsync(() => WaitFor(task)));
        }

        Assert.Equal(["first", "second"], notified);
        Assert.Equal(1, first.FaultCount);
        Assert.Equal(1, second.FaultCount);
        Assert.Contains(logger.ReceivedCalls(), call =>
            call.GetMethodInfo().Name == nameof(ILogger.Log)
            && Equals(call.GetArguments()[0], LogLevel.Error)
            && ReferenceEquals(call.GetArguments()[3], notificationFailure));
        var late = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.WriteStateAsync(CancellationToken.None).AsTask());
        Assert.Same(expected, late.InnerException);
        Assert.Empty(storage.Appends);
    }

    [Fact]
    public async Task Fault_CallbackCanCoordinateCrossThreadManagerReentry()
    {
        var expected = new IOException("Original storage failure.");
        var storage = new CapturingStorage { BlockNextAppend = true, NextAppendException = expected };
        await using var manager = CreateTestSystem(storage).Manager;
        var first = new HookState();
        var second = new HookState();
        manager.RegisterState("first", first);
        manager.RegisterState("second", second);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        var current = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await WaitFor(storage.BlockedAppendStarted.Task);
        var queued = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        var callbackEntered = NewSignal();
        var workerReady = NewSignal();
        var worker = Task.Run(async () =>
        {
            workerReady.SetResult();
            await WaitFor(callbackEntered.Task);
            return (ThreadId: Environment.CurrentManagedThreadId,
                Failure: Record.Exception(() => manager.RegisterState("late", new HookState())));
        }, TestContext.Current.CancellationToken);
        await WaitFor(workerReady.Task);
        Exception? callbackError = null;
        var notifications = new List<string>();
        first.FaultAction = exception =>
        {
            callbackError = Record.Exception(() =>
            {
                Assert.Same(expected, exception);
                callbackEntered.SetResult();
                var result = worker.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).GetAwaiter().GetResult();
                Assert.NotEqual(Environment.CurrentManagedThreadId, result.ThreadId);
                Assert.Same(expected, Assert.IsType<InvalidOperationException>(result.Failure).InnerException);
                Assert.False(current.IsCompleted);
                Assert.False(queued.IsCompleted);
                notifications.Add("first");
            });
        };
        second.FaultAction = exception =>
        {
            Assert.Same(expected, exception);
            Assert.False(current.IsCompleted);
            Assert.False(queued.IsCompleted);
            notifications.Add("second");
        };

        storage.ReleaseAppend.SetResult();
        Assert.Same(expected, await Record.ExceptionAsync(() => WaitFor(current)));
        Assert.Same(expected, await Record.ExceptionAsync(() => WaitFor(queued)));
        await WaitFor(worker);
        Assert.Null(callbackError);
        Assert.Equal(["first", "second"], notifications);
        Assert.Equal(1, first.FaultCount);
        Assert.Equal(1, second.FaultCount);
        Assert.False(manager.TryGetState("late", out _));
    }

    [Fact]
    public async Task LatchedFailure_PreservesAlreadyCapturedWriteAcknowledgement()
    {
        var storage = new CapturingStorage { BlockNextAppend = true };
        await using var manager = CreateTestSystem(storage).Manager;
        var state = new HookState();
        manager.RegisterState("state", state);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        var capturedWrite = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await WaitFor(storage.BlockedAppendStarted.Task);
        var expected = new IOException("Failure after capture.");
        state.ReadinessAction = () => throw expected;
        var failingWrite = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        Assert.Null(state.Failure);
        Assert.Equal(0, state.WriteCompletedCount);
        storage.ReleaseAppend.SetResult();
        await WaitFor(capturedWrite);
        Assert.Same(expected, await Record.ExceptionAsync(() => WaitFor(failingWrite)));
        Assert.Equal(1, state.CaptureCount);
        Assert.Equal(1, state.WriteCompletedCount);
        Assert.Same(expected, state.Failure);
        Assert.Single(storage.Appends);
    }

    [Fact]
    public async Task InitializationFailure_NotifiesRegisteredStatesOnce()
    {
        var expected = new IOException("Recovery failed.");
        var storage = new CapturingStorage { NextReadException = expected };
        await using var manager = CreateTestSystem(storage).Manager;
        var state = new HookState();
        manager.RegisterState("state", state);
        Assert.Same(expected, await Record.ExceptionAsync(() => manager.InitializeAsync(CancellationToken.None).AsTask()));
        Assert.Same(expected, state.Failure);
        var retry = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.InitializeAsync(CancellationToken.None).AsTask());
        Assert.Same(expected, retry.InnerException);
        Assert.Equal(1, state.FaultCount);
    }

    [Fact]
    public async Task IdleShutdown_CompletesWithoutFaultNotification()
    {
        var sut = CreateTestSystem();
        await using var manager = sut.Manager;
        var state = new HookState();
        manager.RegisterState("state", state);
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        await sut.Lifecycle.OnStop(TestContext.Current.CancellationToken);
        Assert.Null(state.Failure);
        Assert.Equal(0, state.FaultCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdmittedShutdownCancellation_NotifiesAndFaultsWaiters(bool preparation)
    {
        var storage = new CapturingStorage { BlockNextAppend = !preparation };
        var sut = CreateTestSystem(storage);
        await using var manager = sut.Manager;
        var state = new HookState { Prepared = !preparation };
        var entered = NewSignal();
        state.PrepareAction = async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        manager.RegisterState("state", state);
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        var write = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await WaitFor(preparation ? entered.Task : storage.BlockedAppendStarted.Task);
        var queued = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await sut.Lifecycle.OnStop(TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WaitFor(write));
        Assert.Same(exception, state.Failure);
        Assert.Same(exception, await Record.ExceptionAsync(() => WaitFor(queued)));
        Assert.Equal(1, state.FaultCount);
        Assert.Equal(0, state.WriteCompletedCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellation_PreservesOwnedPreparationStorageAndAck(bool cancelDuringPreparation)
    {
        var storage = new CapturingStorage { BlockNextAppend = true };
        await using var manager = CreateTestSystem(storage).Manager;
        var state = new HookState { Prepared = false };
        var entered = NewSignal();
        var release = NewSignal();
        var acknowledged = NewSignal();
        var ownedToken = CancellationToken.None;
        state.PrepareAction = async token =>
        {
            ownedToken = token;
            entered.SetResult();
            await release.Task.WaitAsync(token);
            state.Prepared = true;
        };
        state.WriteCompletedAction = () => acknowledged.SetResult();
        manager.RegisterState("state", state);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        using var caller = new CancellationTokenSource();
        var write = manager.WriteStateAsync(caller.Token).AsTask();
        await WaitFor(entered.Task);
        Assert.True(ownedToken.CanBeCanceled);
        Assert.NotEqual(caller.Token, ownedToken);
        if (cancelDuringPreparation)
        {
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
            Assert.False(ownedToken.IsCancellationRequested);
        }

        release.SetResult();
        await WaitFor(storage.BlockedAppendStarted.Task);
        if (!cancelDuringPreparation)
        {
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        }

        Assert.False(ownedToken.IsCancellationRequested);
        Assert.False(acknowledged.Task.IsCompleted);
        state.EmitEntry = false;
        var nextWrite = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        storage.ReleaseAppend.SetResult();
        await WaitFor(acknowledged.Task);
        await WaitFor(nextWrite);
        Assert.Equal(1, state.PrepareCount);
        Assert.Equal(1, state.WriteCompletedCount);
        Assert.Equal(0, manager.PendingWriteByteCount);
        Assert.Null(state.Failure);
        Assert.Single(storage.Appends);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task WaitFor(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly Channel<(SendOrPostCallback Callback, object? State)> _queue =
            Channel.CreateUnbounded<(SendOrPostCallback, object?)>();

        public int Turn { get; private set; }

        public override void Post(SendOrPostCallback callback, object? state) => Assert.True(_queue.Writer.TryWrite((callback, state)));

        public async Task Run(Func<Task> action)
        {
            Task task = Task.CompletedTask;
            Invoke(_ => task = action(), null);
            while (!task.IsCompleted)
            {
                var next = await _queue.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                Invoke(next.Callback, next.State);
            }

            await task;
        }

        private void Invoke(SendOrPostCallback callback, object? state)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                Turn++;
                callback(state);
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }

    private sealed class HookState : IJournaledState
    {
        public bool Prepared { get; set; } = true;
        public bool EmitEntry { get; set; } = true;
        public Func<bool>? ReadinessAction { get; set; }
        public Func<CancellationToken, ValueTask>? PrepareAction { get; set; }
        public Action? ValidateWriteAction { get; set; }
        public Action? ValidateDeleteAction { get; set; }
        public Action? DeleteStartedAction { get; set; }
        public Action? ResetAction { get; set; }
        public Action? CaptureAction { get; set; }
        public Action? WriteCompletedAction { get; set; }
        public Action<Exception>? FaultAction { get; set; }
        public JournalStreamWriter Writer { get; private set; }
        public int ResetCount { get; private set; }
        public int PrepareCount { get; private set; }
        public int CaptureCount { get; private set; }
        public int DeleteStartedCount { get; private set; }
        public int WriteCompletedCount { get; private set; }
        public int FaultCount { get; private set; }
        public Exception? Failure { get; private set; }
        public bool IsWritePrepared => ReadinessAction?.Invoke() ?? Prepared;

        public ValueTask PrepareWriteAsync(CancellationToken cancellationToken)
        {
            PrepareCount++;
            if (PrepareAction is { } prepare) return prepare(cancellationToken);
            Prepared = true;
            return default;
        }

        public void ValidateWrite() => ValidateWriteAction?.Invoke();
        public void ValidateDelete() => ValidateDeleteAction?.Invoke();

        public void OnDeleteStarted()
        {
            DeleteStartedCount++;
            DeleteStartedAction?.Invoke();
        }

        public void Reset(JournalStreamWriter writer)
        {
            Writer = writer;
            ResetCount++;
            ResetAction?.Invoke();
        }

        public void AppendEntries(JournalStreamWriter writer)
        {
            CaptureAction?.Invoke();
            CaptureCount++;
            if (EmitEntry)
            {
                using var entry = writer.BeginEntry();
                entry.Writer.GetSpan(1)[0] = 1;
                entry.Writer.Advance(1);
                entry.Commit();
            }
        }

        public void AppendSnapshot(JournalStreamWriter writer) => AppendEntries(writer);

        public void OnWriteCompleted()
        {
            WriteCompletedCount++;
            WriteCompletedAction?.Invoke();
        }

        public void OnFaulted(Exception exception)
        {
            FaultCount++;
            Failure = exception;
            FaultAction?.Invoke(exception);
        }

        public void ReplayEntry(JournalEntry entry, JournalReplayContext context) => throw new NotSupportedException();
        public IJournaledState DeepCopy() => throw new NotSupportedException();
    }
}
