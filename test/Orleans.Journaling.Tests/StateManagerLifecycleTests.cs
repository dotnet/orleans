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
        IStateMachine state = new AlwaysWritingState();
        state.ValidatePendingChanges();
        state.ValidateWrite();
        state.ValidateDelete();
        state.OnDeleteStarted();
        state.OnFaulted(new IOException("Default notification."));

        var storage = new CapturingStorage();
        await using var manager = CreateTestSystem(storage).Manager;
        manager.RegisterStateMachine("state", state);
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
        manager.RegisterStateMachine("state", state);
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
        manager.RegisterStateMachine("first", first);
        manager.RegisterStateMachine("second", second);
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
        manager.RegisterStateMachine("first", first);
        manager.RegisterStateMachine("second", second);
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
    public async Task PendingValidation_ValidatesAllStatesBeforeAnyCapture(string order, bool snapshot)
    {
        var storage = new CapturingStorage { IsCompactionRequested = snapshot };
        var format = new TrackingJournalFormat(SessionPool);
        await using var manager = CreateTestSystem(storage, journalFormat: format).Manager;
        var states = new Dictionary<char, HookState> { ['a'] = new(), ['b'] = new(), ['c'] = new() };
        var events = new List<string>();
        foreach (var key in order)
        {
            var state = states[key];
            manager.RegisterStateMachine(key.ToString(), state);
            state.CaptureAction = () =>
            {
                Assert.All(states.Values, value => Assert.Equal(1, value.PendingValidationCount));
                events.Add($"capture {key}");
            };
        }

        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        var initialWriter = Assert.Single(format.Writers);
        var initialEntries = initialWriter.BeganEntryIds.Count;
        foreach (var key in order)
        {
            states[key].PendingValidationAction = () =>
            {
                Assert.Same(initialWriter, Assert.Single(format.Writers));
                Assert.Equal(initialEntries, initialWriter.BeganEntryIds.Count);
                Assert.All(states.Values, value => Assert.Equal(0, value.CaptureCount));
                events.Add($"validate {key}");
            };
        }

        await manager.WriteStateAsync(TestContext.Current.CancellationToken);

        Assert.All(states.Values, state =>
        {
            Assert.Equal(1, state.PendingValidationCount);
            Assert.Equal(1, state.CaptureCount);
            Assert.Equal(1, state.WriteCompletedCount);
        });
        Assert.Equal(order.Select(key => $"validate {key}").Concat(order.Select(key => $"capture {key}")), events);
        Assert.Equal(snapshot ? 1 : 0, storage.Replaces.Count);
        Assert.Equal(snapshot ? 0 : 1, storage.Appends.Count);
    }

    [Fact]
    public async Task PendingValidation_AndCaptureShareSchedulerTurn()
    {
        var context = new QueuedSynchronizationContext();
        await context.Run(async () =>
        {
            await using var manager = CreateTestSystem().Manager;
            var state = new HookState();
            var validationTurn = -1;
            state.PendingValidationAction = () => validationTurn = context.Turn;
            state.CaptureAction = () => Assert.Equal(validationTurn, context.Turn);
            manager.RegisterStateMachine("state", state);
            await manager.InitializeAsync(TestContext.Current.CancellationToken);
            await manager.WriteStateAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, state.PendingValidationCount);
            Assert.Equal(1, state.CaptureCount);
            Assert.Equal(1, state.WriteCompletedCount);
        });
    }

    [Fact]
    public async Task ZeroByteWrite_ValidatesPendingChangesWithoutWriteCompleted()
    {
        var storage = new CapturingStorage();
        await using var manager = CreateTestSystem(storage).Manager;
        var state = new HookState { EmitEntry = false };
        manager.RegisterStateMachine("state", state);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, manager.PendingWriteByteCount);
        Assert.Equal(1, state.WriteCompletedCount);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, state.PendingValidationCount);
        Assert.Equal(2, state.CaptureCount);
        Assert.Equal(1, state.WriteCompletedCount);
        Assert.Single(storage.Appends);
    }

    [Theory]
    [InlineData("append", false, false)]
    [InlineData("append", false, true)]
    [InlineData("append", true, false)]
    [InlineData("append", true, true)]
    [InlineData("snapshot", false, false)]
    [InlineData("snapshot", false, true)]
    [InlineData("snapshot", true, false)]
    [InlineData("snapshot", true, true)]
    [InlineData("empty-buffer", false, false)]
    [InlineData("empty-buffer", false, true)]
    [InlineData("empty-buffer", true, false)]
    [InlineData("empty-buffer", true, true)]
    public async Task PartialApplyFailure_FencesBeforeCapture(string path, bool previouslyAdmitted, bool stateFirst)
    {
        var context = new QueuedSynchronizationContext();
        await context.Run(async () =>
        {
            var storage = new CapturingStorage();
            var format = new TrackingJournalFormat(SessionPool);
            await using var manager = CreateTestSystem(storage, journalFormat: format).Manager;
            var state = new HookState { EmitEntry = false };
            var other = new HookState { EmitEntry = false };
            if (stateFirst) manager.RegisterStateMachine("state", state);
            var value = new DurableValue<int>("business", manager, CreateValueCodec<int>());
            manager.RegisterStateMachine("other", other);
            if (!stateFirst) manager.RegisterStateMachine("state", state);
            await manager.InitializeAsync(TestContext.Current.CancellationToken);
            if (path == "empty-buffer")
            {
                await manager.WriteStateAsync(TestContext.Current.CancellationToken);
                Assert.Equal(0, manager.PendingWriteByteCount);
            }

            storage.IsCompactionRequested = path == "snapshot";
            var previousWrites = storage.Appends.Count;
            var previousCaptures = state.CaptureCount;
            var previousAcks = state.WriteCompletedCount;
            var initialWriter = Assert.Single(format.Writers);
            var initialEntries = initialWriter.BeganEntryIds.Count;
            var waiters = new List<Task>();
            var expected = new InvalidOperationException("Synchronous apply failed after changing business state.");
            Exception? latchedFailure = null;
            var handlerContext = new AsyncLocal<bool>();
            var admissions = 0;
            state.ValidateWriteAction = () =>
            {
                Assert.False(handlerContext.Value);
                admissions++;
            };
            state.PendingValidationAction = () =>
            {
                Assert.Same(initialWriter, Assert.Single(format.Writers));
                Assert.Equal(initialEntries, initialWriter.BeganEntryIds.Count);
                Assert.Same(expected, latchedFailure);
                throw latchedFailure!;
            };
            if (previouslyAdmitted) waiters.Add(manager.WriteStateAsync(CancellationToken.None).AsTask());
            handlerContext.Value = true;
            try
            {
                value.Value = 42;
                throw expected;
            }
            catch (InvalidOperationException exception)
            {
                latchedFailure = exception;
            }
            finally
            {
                handlerContext.Value = false;
            }

            if (path == "empty-buffer") Assert.Equal(0, manager.PendingWriteByteCount);
            waiters.Add(manager.WriteStateAsync(CancellationToken.None).AsTask());
            waiters.Add(manager.DeleteStateAsync(CancellationToken.None).AsTask());
            waiters.Add(manager.InitializeAsync(CancellationToken.None).AsTask());
            var notifications = new List<string>();
            var stateOwnedWaiter = NewSignal();
            state.FaultAction = exception =>
            {
                Assert.Same(expected, exception);
                Assert.All(waiters, waiter => Assert.False(waiter.IsCompleted));
                stateOwnedWaiter.TrySetException(exception);
                notifications.Add("state");
            };
            other.FaultAction = exception =>
            {
                Assert.Same(expected, exception);
                Assert.All(waiters, waiter => Assert.False(waiter.IsCompleted));
                notifications.Add("other");
            };
            Assert.Equal(previouslyAdmitted ? 2 : 1, admissions);
            foreach (var waiter in waiters)
            {
                Assert.Same(expected, await Record.ExceptionAsync(() => WaitFor(waiter)));
            }

            Assert.Same(expected, await Record.ExceptionAsync(() => WaitFor(stateOwnedWaiter.Task)));
            Assert.Equal(stateFirst ? new[] { "state", "other" } : ["other", "state"], notifications);
            Assert.Equal(42, value.Value);
            Assert.Equal(previousCaptures, state.CaptureCount);
            Assert.Equal(previousAcks, state.WriteCompletedCount);
            Assert.Equal(previousWrites, storage.Appends.Count);
            Assert.Empty(storage.Replaces);
            Assert.Equal(0, storage.DeleteCount);
            var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask());
            Assert.Same(expected, rejected.InnerException);
        });
    }

    [Fact]
    public async Task PendingValidation_AllowsIndependentWriteDuringLocalPreparation()
    {
        var context = new QueuedSynchronizationContext();
        await context.Run(async () =>
        {
            var storage = new CapturingStorage();
            await using var manager = CreateTestSystem(storage).Manager;
            var state = new HookState();
            manager.RegisterStateMachine("state", state);
            await manager.InitializeAsync(TestContext.Current.CancellationToken);
            var handlerContext = new AsyncLocal<bool>();
            var preparing = false;
            var admissionCalls = 0;
            var rejected = new InvalidOperationException("Handler preparation cannot commit state.");
            state.ValidateWriteAction = () =>
            {
                admissionCalls++;
                if (handlerContext.Value) throw rejected;
            };
            state.PendingValidationAction = () =>
            {
                Assert.True(preparing);
                Assert.False(handlerContext.Value);
                Assert.Equal(2, admissionCalls);
            };

            var independent = manager.WriteStateAsync(CancellationToken.None).AsTask();
            preparing = true;
            handlerContext.Value = true;
            Assert.Same(rejected, await Record.ExceptionAsync(() => manager.WriteStateAsync(CancellationToken.None).AsTask()));
            await independent;
            Assert.Equal(1, state.PendingValidationCount);
            Assert.Equal(1, state.WriteCompletedCount);
            Assert.Single(storage.Appends);
            Assert.Null(state.Failure);

            handlerContext.Value = false;
            preparing = false;
            state.PendingValidationAction = null;
            await manager.WriteStateAsync(CancellationToken.None);
            Assert.Equal(3, admissionCalls);
            Assert.Equal(2, state.WriteCompletedCount);
            Assert.Null(state.Failure);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommittedOnlyWrite_ChecksLatchedStateFailure(bool emptyPrefix)
    {
        var storage = new CapturingStorage();
        await using var manager = CreateTestSystem(storage).Manager;
        var state = new HookState();
        manager.RegisterStateMachine("state", state);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        if (emptyPrefix)
        {
            await manager.WriteStateAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, manager.PendingWriteByteCount);
        }

        var previousCaptures = state.CaptureCount;
        var previousWrites = storage.Appends.Count;
        var expected = new IOException("Latched state failure.");
        state.PendingValidationAction = () => throw expected;
        var write = StartWhileEntryIsOpen();
        Assert.Same(expected, await Record.ExceptionAsync(() => WaitFor(write)));
        Assert.Same(expected, state.Failure);
        Assert.Equal(previousCaptures, state.CaptureCount);
        Assert.Equal(previousWrites, storage.Appends.Count);

        Task StartWhileEntryIsOpen()
        {
            using var entry = state.Writer.BeginEntry();
            var result = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
            using var completed = new ManualResetEventSlim();
            result.GetAwaiter().OnCompleted(completed.Set);
            Assert.True(completed.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken),
                "The committed-only write must validate pending changes while the lexical entry is open.");
            return result;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fault_NotifiesEveryStateBeforeWaitersAndPreservesOriginal(bool failValidation)
    {
        var context = new QueuedSynchronizationContext();
        await context.Run(async () =>
        {
            var expected = new IOException("Original failure.");
            var notificationFailure = new InvalidOperationException("Notification failed.");
            var storage = new CapturingStorage { BlockNextAppend = !failValidation, NextAppendException = expected };
            var logger = Substitute.For<ILogger>();
            logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
            var loggerFactory = Substitute.For<ILoggerFactory>();
            loggerFactory.CreateLogger(Arg.Any<string>()).Returns(logger);
            var shared = new JournaledStateManagerShared(new Logger<JournaledStateManager>(loggerFactory),
                Options.Create(ManagerOptions), TimeProvider.System, ServiceProvider);
            await using var manager = new JournaledStateManager(shared, storage);
            var first = new HookState();
            var second = new HookState();
            manager.RegisterStateMachine("first", first);
            manager.RegisterStateMachine("second", second);
            if (failValidation) first.PendingValidationAction = () => throw expected;
            await manager.InitializeAsync(TestContext.Current.CancellationToken);
            var current = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
            if (!failValidation) await WaitFor(storage.BlockedAppendStarted.Task);
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
                var rejected = Assert.Throws<InvalidOperationException>(() => manager.RegisterStateMachine("late", new HookState()));
                Assert.Same(expected, rejected.InnerException);
                notified.Add("second");
            };

            if (!failValidation) storage.ReleaseAppend.SetResult();
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
        });
    }

    [Fact]
    public async Task Fault_CallbackCanCoordinateCrossThreadManagerReentry()
    {
        var expected = new IOException("Original storage failure.");
        var storage = new CapturingStorage { BlockNextAppend = true, NextAppendException = expected };
        await using var manager = CreateTestSystem(storage).Manager;
        var first = new HookState();
        var second = new HookState();
        manager.RegisterStateMachine("first", first);
        manager.RegisterStateMachine("second", second);
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
                Failure: Record.Exception(() => manager.RegisterStateMachine("late", new HookState())));
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
        var lookup = Assert.Throws<InvalidOperationException>(() => manager.TryGetStateMachine("late", out _));
        Assert.Same(expected, lookup.InnerException);
    }

    [Fact]
    public async Task LatchedFailure_PreservesAlreadyCapturedWriteAcknowledgement()
    {
        var storage = new CapturingStorage { BlockNextAppend = true };
        await using var manager = CreateTestSystem(storage).Manager;
        var state = new HookState();
        manager.RegisterStateMachine("state", state);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        var capturedWrite = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await WaitFor(storage.BlockedAppendStarted.Task);
        var expected = new IOException("Failure after capture.");
        state.PendingValidationAction = () => throw expected;
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
        manager.RegisterStateMachine("state", state);
        Assert.Same(expected, await Record.ExceptionAsync(() => manager.InitializeAsync(CancellationToken.None).AsTask()));
        Assert.Same(expected, state.Failure);
        var retry = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.InitializeAsync(CancellationToken.None).AsTask());
        Assert.Same(expected, retry.InnerException);
        Assert.Equal(1, state.FaultCount);
    }

    [Fact]
    public async Task InitializationCallerCancellation_LeavesOwnedRecoveryRunning()
    {
        var storage = new MutableReadStorage(1, Array.Empty<byte>());
        await using var manager = CreateTestSystem(storage).Manager;
        var state = new HookState();
        manager.RegisterStateMachine("state", state);
        using var caller = new CancellationTokenSource();
        var canceledWaiter = manager.InitializeAsync(caller.Token).AsTask();
        await WaitFor(storage.BlockedReadStarted.Task);
        var remainingWaiter = manager.InitializeAsync(CancellationToken.None).AsTask();
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WaitFor(canceledWaiter));
        Assert.False(storage.ReadToken.IsCancellationRequested);
        Assert.False(remainingWaiter.IsCompleted);
        Assert.Equal(0, state.FaultCount);
        Assert.Equal(0, state.RecoveryCompletedCount);

        storage.AllowBlockedRead.SetResult();
        await WaitFor(remainingWaiter);
        Assert.Equal(1, state.RecoveryCompletedCount);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["append"], storage.OperationLog);
        Assert.Equal(1, state.WriteCompletedCount);
        Assert.Null(state.Failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryShutdown_CancelsInitializationWithoutFaultNotification(bool lifecycleStop)
    {
        var storage = new MutableReadStorage(1, Array.Empty<byte>());
        var sut = CreateTestSystem(storage);
        await using var manager = sut.Manager;
        var first = new HookState();
        var second = new HookState();
        manager.RegisterStateMachine("first", first);
        manager.RegisterStateMachine("second", second);
        var startup = lifecycleStop
            ? sut.Lifecycle.OnStart(CancellationToken.None)
            : manager.InitializeAsync(CancellationToken.None).AsTask();
        await WaitFor(storage.BlockedReadStarted.Task);
        var another = manager.InitializeAsync(CancellationToken.None).AsTask();
        Assert.False(startup.IsCompleted);
        Assert.False(another.IsCompleted);

        await WaitFor(lifecycleStop
            ? sut.Lifecycle.OnStop(TestContext.Current.CancellationToken)
            : manager.DisposeAsync().AsTask());
        foreach (var waiter in new[] { startup, another })
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WaitFor(waiter));
            Assert.True(waiter.IsCanceled);
        }

        Assert.True(storage.ReadToken.IsCancellationRequested);
        Assert.False(storage.AllowBlockedRead.Task.IsCompleted);
        Assert.Empty(storage.OperationLog);
        Assert.All(new[] { first, second }, state =>
        {
            Assert.Equal(0, state.FaultCount);
            Assert.Equal(0, state.RecoveryCompletedCount);
            Assert.Equal(0, state.PendingValidationCount);
            Assert.Equal(0, state.CaptureCount);
            Assert.Equal(0, state.WriteCompletedCount);
            Assert.Null(state.Failure);
        });
        if (lifecycleStop)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.WriteStateAsync(CancellationToken.None).AsTask());
        }

        await WaitFor(manager.DisposeAsync().AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.InitializeAsync(CancellationToken.None).AsTask());
    }

    [Theory]
    [InlineData("provider-cancellation")]
    [InlineData("provider-io")]
    [InlineData("replay")]
    public async Task RecoveryFailure_FencesAndNotifiesAllStates(string failure)
    {
        var context = new QueuedSynchronizationContext();
        await context.Run(async () =>
        {
            Exception expected = failure == "provider-cancellation"
                ? new OperationCanceledException("Provider read cancellation.", new CancellationToken(canceled: true))
                : new IOException("Provider read failure.");
            IJournalStorage storage = failure == "replay"
                ? new RawReadStorage([1, 2, 3])
                : new CapturingStorage { NextReadException = expected };
            await using var manager = CreateTestSystem(storage).Manager;
            var states = new[] { new HookState(), new HookState() };
            manager.RegisterStateMachine("first", states[0]);
            manager.RegisterStateMachine("second", states[1]);
            var waiters = new[]
            {
                manager.InitializeAsync(CancellationToken.None).AsTask(),
                manager.InitializeAsync(CancellationToken.None).AsTask()
            };
            var notified = new List<Exception>();
            foreach (var state in states)
            {
                state.FaultAction = exception =>
                {
                    Assert.All(waiters, waiter => Assert.False(waiter.IsCompleted));
                    notified.Add(exception);
                };
            }

            var observed = await Record.ExceptionAsync(() => WaitFor(waiters[0]));
            if (failure == "replay")
            {
                Assert.Contains("Failed to recover journaling state", Assert.IsType<InvalidOperationException>(observed).Message);
                Assert.NotNull(observed.InnerException);
            }
            else
            {
                Assert.Same(expected, observed);
            }

            Assert.Same(observed, await Record.ExceptionAsync(() => WaitFor(waiters[1])));
            Assert.Equal(2, notified.Count);
            Assert.All(notified, exception => Assert.Same(observed, exception));
            Assert.All(states, state =>
            {
                Assert.Same(observed, state.Failure);
                Assert.Equal(1, state.FaultCount);
                Assert.Equal(0, state.RecoveryCompletedCount);
            });
            var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.InitializeAsync(CancellationToken.None).AsTask());
            Assert.Same(observed, rejected.InnerException);
        });
    }

    [Fact]
    public async Task RecoveryIoFailure_DuringShutdownPreservesOriginalCause()
    {
        var expected = new IOException("Read failure during shutdown.");
        var entered = NewSignal();
        var storage = Substitute.For<IJournalStorage>();
        storage.ReadAsync(Arg.Any<IJournalStorageConsumer>(), Arg.Any<CancellationToken>())
            .Returns(call => ReadAsync(call.Arg<CancellationToken>()));
        await using var manager = CreateTestSystem(storage).Manager;
        var state = new HookState();
        manager.RegisterStateMachine("state", state);
        var initializing = manager.InitializeAsync(CancellationToken.None).AsTask();
        await WaitFor(entered.Task);
        var another = manager.InitializeAsync(CancellationToken.None).AsTask();
        await WaitFor(manager.DisposeAsync().AsTask());
        Assert.Same(expected, await Record.ExceptionAsync(() => WaitFor(initializing)));
        Assert.Same(expected, await Record.ExceptionAsync(() => WaitFor(another)));
        Assert.Same(expected, state.Failure);
        Assert.Equal(1, state.FaultCount);
        Assert.Equal(0, state.RecoveryCompletedCount);

        async ValueTask ReadAsync(CancellationToken token)
        {
            entered.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw expected;
            }
        }
    }

    [Fact]
    public async Task IdleShutdown_CompletesWithoutFaultNotification()
    {
        var sut = CreateTestSystem();
        await using var manager = sut.Manager;
        var state = new HookState();
        manager.RegisterStateMachine("state", state);
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        await sut.Lifecycle.OnStop(TestContext.Current.CancellationToken);
        Assert.Null(state.Failure);
        Assert.Equal(0, state.FaultCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdmittedShutdownCancellation_NotifiesAndFaultsWaiters(bool snapshot)
    {
        var storage = new CapturingStorage
        {
            IsCompactionRequested = snapshot,
            BlockNextAppend = !snapshot,
            BlockNextReplace = snapshot
        };
        var sut = CreateTestSystem(storage);
        await using var manager = sut.Manager;
        var state = new HookState();
        manager.RegisterStateMachine("state", state);
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        var write = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await WaitFor(snapshot ? storage.ReplaceEntered.Task : storage.BlockedAppendStarted.Task);
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
    public async Task CallerCancellation_PreservesOwnedStorageAndAck(bool snapshot)
    {
        var storage = new CapturingStorage
        {
            IsCompactionRequested = snapshot,
            BlockNextAppend = !snapshot,
            BlockNextReplace = snapshot
        };
        await using var manager = CreateTestSystem(storage).Manager;
        var state = new HookState();
        var acknowledged = NewSignal();
        state.WriteCompletedAction = () => acknowledged.SetResult();
        manager.RegisterStateMachine("state", state);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        using var caller = new CancellationTokenSource();
        var write = manager.WriteStateAsync(caller.Token).AsTask();
        await WaitFor(snapshot ? storage.ReplaceEntered.Task : storage.BlockedAppendStarted.Task);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        Assert.False(acknowledged.Task.IsCompleted);
        state.EmitEntry = false;
        storage.IsCompactionRequested = false;
        var nextWrite = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        if (snapshot) storage.ReleaseReplace.SetResult();
        else storage.ReleaseAppend.SetResult();
        await WaitFor(acknowledged.Task);
        await WaitFor(nextWrite);
        Assert.Equal(2, state.PendingValidationCount);
        Assert.Equal(1, state.WriteCompletedCount);
        Assert.Equal(0, manager.PendingWriteByteCount);
        Assert.Null(state.Failure);
        Assert.Equal(snapshot ? 0 : 1, storage.Appends.Count);
        Assert.Equal(snapshot ? 1 : 0, storage.Replaces.Count);
    }

    [Fact]
    public async Task AdmittedDeleteShutdownCancellation_NotifiesAndFaultsWaiters()
    {
        var storage = new BlockingDeleteStorage();
        var sut = CreateTestSystem(storage);
        await using var manager = sut.Manager;
        var state = new HookState();
        manager.RegisterStateMachine("state", state);
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        var deleting = manager.DeleteStateAsync(CancellationToken.None).AsTask();
        await WaitFor(storage.FirstDeleteStarted.Task);
        var queued = manager.WriteStateAsync(CancellationToken.None).AsTask();
        var notified = false;
        state.FaultAction = _ =>
        {
            Assert.False(deleting.IsCompleted);
            Assert.False(queued.IsCompleted);
            notified = true;
        };
        await WaitFor(sut.Lifecycle.OnStop(TestContext.Current.CancellationToken));
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WaitFor(deleting));
        Assert.Same(exception, state.Failure);
        Assert.Same(exception, await Record.ExceptionAsync(() => WaitFor(queued)));
        Assert.True(notified);
        Assert.Equal(1, state.FaultCount);
        Assert.Equal(1, state.DeleteStartedCount);
        Assert.Equal(1, state.ResetCount);
        Assert.Equal(0, state.WriteCompletedCount);
    }

    [Fact]
    public async Task CallerCancellation_PreservesQueuedWrite()
    {
        var context = new QueuedSynchronizationContext();
        await context.Run(async () =>
        {
            var storage = new CapturingStorage();
            await using var manager = CreateTestSystem(storage).Manager;
            var state = new HookState();
            manager.RegisterStateMachine("state", state);
            await manager.InitializeAsync(TestContext.Current.CancellationToken);
            using var caller = new CancellationTokenSource();
            var write = manager.WriteStateAsync(caller.Token).AsTask();
            Assert.Equal(0, state.PendingValidationCount);
            caller.Cancel();
            var remainingWaiter = manager.WriteStateAsync(CancellationToken.None).AsTask();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
            await remainingWaiter;
            Assert.Single(storage.Appends);
            Assert.Equal(1, state.PendingValidationCount);
            Assert.Equal(1, state.WriteCompletedCount);
            Assert.Null(state.Failure);
        });
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

    private sealed class HookState : IStateMachine
    {
        public bool EmitEntry { get; set; } = true;
        public Action? PendingValidationAction { get; set; }
        public Action? ValidateWriteAction { get; set; }
        public Action? ValidateDeleteAction { get; set; }
        public Action? DeleteStartedAction { get; set; }
        public Action? ResetAction { get; set; }
        public Action? CaptureAction { get; set; }
        public Action? WriteCompletedAction { get; set; }
        public Action<Exception>? FaultAction { get; set; }
        public JournalStreamWriter Writer { get; private set; }
        public int ResetCount { get; private set; }
        public int PendingValidationCount { get; private set; }
        public int CaptureCount { get; private set; }
        public int DeleteStartedCount { get; private set; }
        public int WriteCompletedCount { get; private set; }
        public int FaultCount { get; private set; }
        public int RecoveryCompletedCount { get; private set; }
        public Exception? Failure { get; private set; }
        public void ValidatePendingChanges()
        {
            PendingValidationCount++;
            PendingValidationAction?.Invoke();
        }

        public void ValidateWrite() => ValidateWriteAction?.Invoke();
        public void ValidateDelete() => ValidateDeleteAction?.Invoke();
        public void OnRecoveryCompleted() => RecoveryCompletedCount++;

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

        public void WritePendingEntries(JournalStreamWriter writer)
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

        public void WriteSnapshot(JournalStreamWriter writer) => WritePendingEntries(writer);

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
    }
}
