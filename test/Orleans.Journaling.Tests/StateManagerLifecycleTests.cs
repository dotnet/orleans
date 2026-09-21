using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Orleans.Journaling.Tests;

public partial class StateManagerTests
{
    [Fact]
    public async Task StateMachineProtocol_WritesAndResetsAfterDeletion()
    {
        var storage = new CapturingStorage();
        await using var manager = CreateTestSystem(storage).Manager;
        var state = new LifecycleState();
        manager.RegisterStateMachine("state", state);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, state.ResetCount);
        Assert.Equal(1, state.RecoveryCompletedCount);

        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Single(storage.Appends);
        Assert.Equal(1, state.CaptureCount);
        Assert.Equal(1, state.WriteCompletedCount);

        await manager.DeleteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, storage.DeleteCount);
        Assert.Equal(2, state.ResetCount);
        Assert.Equal(1, state.WriteCompletedCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_ResetsStatesBeforeCompletion(bool cancelWait)
    {
        var storage = new BlockingDeleteStorage();
        await using var manager = CreateTestSystem(storage).Manager;
        var first = new LifecycleState();
        var second = new LifecycleState();
        manager.RegisterStateMachine("first", first);
        manager.RegisterStateMachine("second", second);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        using var caller = new CancellationTokenSource();
        var deletion = manager.DeleteStateAsync(caller.Token).AsTask();
        await WaitFor(storage.FirstDeleteStarted.Task);
        Assert.Equal(1, first.ResetCount);
        Assert.Equal(1, second.ResetCount);
        Assert.False(deletion.IsCompleted);
        if (cancelWait)
        {
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deletion);
        }

        var resets = new List<string>();
        var resetCompleted = NewSignal();
        first.ResetAction = () =>
        {
            if (!cancelWait) Assert.False(deletion.IsCompleted);
            resets.Add("first");
        };
        second.ResetAction = () =>
        {
            if (!cancelWait) Assert.False(deletion.IsCompleted);
            resets.Add("second");
            resetCompleted.SetResult();
        };
        storage.AllowFirstDelete.SetResult();
        await WaitFor(resetCompleted.Task);
        if (!cancelWait) await WaitFor(deletion);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["first", "second"], resets);
        Assert.Equal(2, first.ResetCount);
        Assert.Equal(2, second.ResetCount);
        Assert.Equal(1, storage.DeleteCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OperationLocalPreparation_ThenSynchronousUpdatesPersistTogether(bool snapshot)
    {
        var context = new QueuedSynchronizationContext();
        await context.Run(async () =>
        {
            var storage = new CapturingStorage { IsCompactionRequested = snapshot };
            await using var manager = CreateTestSystem(storage).Manager;
            var dictionary = new DurableDictionary<string, int>("items", manager, CreateDictionaryCodec<string, int>());
            var total = new DurableValue<int>("total", manager, CreateValueCodec<int>());
            await manager.InitializeAsync(TestContext.Current.CancellationToken);
            dictionary.Add("existing", 1);
            total.Value = 1;

            var preparing = NewSignal();
            var release = NewSignal();
            var update = PrepareAndApply();
            await WaitFor(preparing.Task);
            await manager.WriteStateAsync(TestContext.Current.CancellationToken);
            Assert.False(update.IsCompleted);
            Assert.Single(dictionary);
            Assert.Equal(1, total.Value);
            await AssertRecovered(1);

            release.SetResult();
            await WaitFor(update);
            Assert.Equal(2, dictionary.Count);
            Assert.Equal(43, total.Value);
            await AssertRecovered(43);

            async Task PrepareAndApply()
            {
                var proposed = (Key: "prepared", Value: 42);
                preparing.SetResult();
                await WaitFor(release.Task);
                Assert.False(dictionary.ContainsKey(proposed.Key));
                dictionary.Add(proposed.Key, proposed.Value);
                total.Value += proposed.Value;
                await manager.WriteStateAsync(TestContext.Current.CancellationToken);
            }

            async Task AssertRecovered(int expectedTotal)
            {
                await using var recovered = CreateTestSystem(storage).Manager;
                var recoveredItems = new DurableDictionary<string, int>("items", recovered, CreateDictionaryCodec<string, int>());
                var recoveredTotal = new DurableValue<int>("total", recovered, CreateValueCodec<int>());
                await recovered.InitializeAsync(TestContext.Current.CancellationToken);
                Assert.Equal(expectedTotal, recoveredTotal.Value);
                Assert.Equal(expectedTotal == 1 ? 1 : 2, recoveredItems.Count);
                Assert.Equal(1, recoveredItems["existing"]);
                if (expectedTotal != 1) Assert.Equal(42, recoveredItems["prepared"]);
            }
        });
    }

    [Fact]
    public async Task ZeroByteWrite_DoesNotRepeatAcknowledgement()
    {
        var storage = new CapturingStorage();
        await using var manager = CreateTestSystem(storage).Manager;
        var state = new LifecycleState { EmitEntry = false };
        manager.RegisterStateMachine("state", state);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, manager.PendingWriteByteCount);
        Assert.Equal(1, state.WriteCompletedCount);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, state.CaptureCount);
        Assert.Equal(1, state.WriteCompletedCount);
        Assert.Single(storage.Appends);
    }

    [Fact]
    public async Task LaterStorageFailure_PreservesAlreadyCapturedWriteAcknowledgement()
    {
        var storage = new CapturingStorage { BlockNextAppend = true };
        await using var manager = CreateTestSystem(storage).Manager;
        var state = new LifecycleState();
        manager.RegisterStateMachine("state", state);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        var capturedWrite = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await WaitFor(storage.BlockedAppendStarted.Task);
        var expected = new IOException("Failure of the next append.");
        storage.NextAppendException = expected;
        var failingWrite = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        Assert.Equal(0, state.WriteCompletedCount);
        storage.ReleaseAppend.SetResult();
        await WaitFor(capturedWrite);
        Assert.Same(expected, await Record.ExceptionAsync(() => WaitFor(failingWrite)));
        Assert.Equal(2, state.CaptureCount);
        Assert.Equal(1, state.WriteCompletedCount);
        Assert.Single(storage.Appends);
        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.WriteStateAsync(CancellationToken.None).AsTask());
        Assert.Same(expected, rejected.InnerException);
    }

    [Fact]
    public async Task DeactivationFailure_PreservesOriginalStorageFailure()
    {
        var expected = new IOException("Original storage failure.");
        var secondary = new InvalidOperationException("Deactivation failed.");
        var storage = new CapturingStorage { BlockNextAppend = true, NextAppendException = expected };
        var provider = Substitute.For<IJournalStorageProvider>();
        var context = Substitute.For<IGrainContext>();
        context.GrainId.Returns(GrainId.Create("test-grain", "deactivation-failure"));
        context.ActivationServices.Returns(ServiceProvider);
        provider.CreateStorage(JournalId.FromGrainId(context.GrainId)).Returns(storage);
        context.When(value => value.Deactivate(Arg.Any<DeactivationReason>(), Arg.Any<CancellationToken>()))
            .Do(_ => throw secondary);
        var shared = new JournaledStateManagerShared(
            ServiceProvider.GetRequiredService<ILogger<JournaledStateManager>>(),
            Options.Create(ManagerOptions), TimeProvider.System, ServiceProvider);
        await using var manager = new JournaledStateManager(shared, provider, context);
        manager.RegisterStateMachine("state", new LifecycleState());
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        var current = manager.WriteStateAsync(CancellationToken.None).AsTask();
        await WaitFor(storage.BlockedAppendStarted.Task);
        var queued = manager.WriteStateAsync(CancellationToken.None).AsTask();
        storage.ReleaseAppend.SetResult();
        Assert.Same(expected, await Record.ExceptionAsync(() => WaitFor(current)));
        Assert.Same(expected, await Record.ExceptionAsync(() => WaitFor(queued)));
        var late = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.InitializeAsync(CancellationToken.None).AsTask());
        Assert.Same(expected, late.InnerException);
        context.Received(1).Deactivate(
            Arg.Is<DeactivationReason>(reason => ReferenceEquals(reason.Exception, expected)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InitializationFailure_PreservesOriginalCause()
    {
        var expected = new IOException("Recovery failed.");
        var storage = new CapturingStorage { NextReadException = expected };
        await using var manager = CreateTestSystem(storage).Manager;
        var state = new LifecycleState();
        manager.RegisterStateMachine("state", state);
        Assert.Same(expected, await Record.ExceptionAsync(() => manager.InitializeAsync(CancellationToken.None).AsTask()));
        var retry = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.InitializeAsync(CancellationToken.None).AsTask());
        Assert.Same(expected, retry.InnerException);
        Assert.Equal(0, state.RecoveryCompletedCount);
    }

    [Fact]
    public async Task InitializationCallerCancellation_LeavesOwnedRecoveryRunning()
    {
        var storage = new MutableReadStorage(1, Array.Empty<byte>());
        await using var manager = CreateTestSystem(storage).Manager;
        var state = new LifecycleState();
        manager.RegisterStateMachine("state", state);
        using var caller = new CancellationTokenSource();
        var canceledWaiter = manager.InitializeAsync(caller.Token).AsTask();
        await WaitFor(storage.BlockedReadStarted.Task);
        var remainingWaiter = manager.InitializeAsync(CancellationToken.None).AsTask();
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WaitFor(canceledWaiter));
        Assert.False(storage.ReadToken.IsCancellationRequested);
        Assert.False(remainingWaiter.IsCompleted);
        Assert.Equal(0, state.RecoveryCompletedCount);

        storage.AllowBlockedRead.SetResult();
        await WaitFor(remainingWaiter);
        Assert.Equal(1, state.RecoveryCompletedCount);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["append"], storage.OperationLog);
        Assert.Equal(1, state.WriteCompletedCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryShutdown_CancelsInitializationWithoutCompletingRecovery(bool lifecycleStop)
    {
        var storage = new MutableReadStorage(1, Array.Empty<byte>());
        var sut = CreateTestSystem(storage);
        await using var manager = sut.Manager;
        var first = new LifecycleState();
        var second = new LifecycleState();
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
            Assert.Equal(0, state.RecoveryCompletedCount);
            Assert.Equal(0, state.CaptureCount);
            Assert.Equal(0, state.WriteCompletedCount);
        });
        if (lifecycleStop)
        {
            Assert.True(manager.TryGetStateMachine("first", out var registered));
            Assert.Same(first, registered);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.WriteStateAsync(CancellationToken.None).AsTask());
        }

        await WaitFor(manager.DisposeAsync().AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.InitializeAsync(CancellationToken.None).AsTask());
    }

    [Theory]
    [InlineData("provider-cancellation")]
    [InlineData("provider-io")]
    [InlineData("replay")]
    public async Task RecoveryFailure_FencesAndFaultsInitializationWaiters(string failure)
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
            var states = new[] { new LifecycleState(), new LifecycleState() };
            manager.RegisterStateMachine("first", states[0]);
            manager.RegisterStateMachine("second", states[1]);
            var waiters = new[]
            {
                manager.InitializeAsync(CancellationToken.None).AsTask(),
                manager.InitializeAsync(CancellationToken.None).AsTask()
            };

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
            Assert.All(states, state => Assert.Equal(0, state.RecoveryCompletedCount));
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
        var state = new LifecycleState();
        manager.RegisterStateMachine("state", state);
        var initializing = manager.InitializeAsync(CancellationToken.None).AsTask();
        await WaitFor(entered.Task);
        var another = manager.InitializeAsync(CancellationToken.None).AsTask();
        await WaitFor(manager.DisposeAsync().AsTask());
        Assert.Same(expected, await Record.ExceptionAsync(() => WaitFor(initializing)));
        Assert.Same(expected, await Record.ExceptionAsync(() => WaitFor(another)));
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
    public async Task IdleShutdown_CompletesWithoutFencing()
    {
        var sut = CreateTestSystem();
        await using var manager = sut.Manager;
        var state = new LifecycleState();
        manager.RegisterStateMachine("state", state);
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        await sut.Lifecycle.OnStop(TestContext.Current.CancellationToken);
        Assert.True(manager.TryGetStateMachine("state", out var registered));
        Assert.Same(state, registered);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.WriteStateAsync(CancellationToken.None).AsTask());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdmittedShutdownCancellation_FencesAndFaultsWaiters(bool snapshot)
    {
        var storage = new CapturingStorage
        {
            IsCompactionRequested = snapshot,
            BlockNextAppend = !snapshot,
            BlockNextReplace = snapshot
        };
        var sut = CreateTestSystem(storage);
        await using var manager = sut.Manager;
        var state = new LifecycleState();
        manager.RegisterStateMachine("state", state);
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        var write = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await WaitFor(snapshot ? storage.ReplaceEntered.Task : storage.BlockedAppendStarted.Task);
        var queued = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await sut.Lifecycle.OnStop(TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WaitFor(write));
        Assert.Same(exception, await Record.ExceptionAsync(() => WaitFor(queued)));
        var rejected = Assert.Throws<InvalidOperationException>(() => manager.TryGetStateMachine("state", out _));
        Assert.Same(exception, rejected.InnerException);
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
        var state = new LifecycleState();
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
        Assert.Equal(2, state.CaptureCount);
        Assert.Equal(1, state.WriteCompletedCount);
        Assert.Equal(0, manager.PendingWriteByteCount);
        Assert.Equal(snapshot ? 0 : 1, storage.Appends.Count);
        Assert.Equal(snapshot ? 1 : 0, storage.Replaces.Count);
    }

    [Fact]
    public async Task AdmittedDeleteShutdownCancellation_FencesAndFaultsWaiters()
    {
        var storage = new BlockingDeleteStorage();
        var sut = CreateTestSystem(storage);
        await using var manager = sut.Manager;
        var state = new LifecycleState();
        manager.RegisterStateMachine("state", state);
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        var deleting = manager.DeleteStateAsync(CancellationToken.None).AsTask();
        await WaitFor(storage.FirstDeleteStarted.Task);
        var queued = manager.WriteStateAsync(CancellationToken.None).AsTask();
        await WaitFor(sut.Lifecycle.OnStop(TestContext.Current.CancellationToken));
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WaitFor(deleting));
        Assert.Same(exception, await Record.ExceptionAsync(() => WaitFor(queued)));
        var rejected = Assert.Throws<InvalidOperationException>(() => manager.TryGetStateMachine("state", out _));
        Assert.Same(exception, rejected.InnerException);
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
            var state = new LifecycleState();
            manager.RegisterStateMachine("state", state);
            await manager.InitializeAsync(TestContext.Current.CancellationToken);
            using var caller = new CancellationTokenSource();
            var write = manager.WriteStateAsync(caller.Token).AsTask();
            Assert.Equal(0, state.CaptureCount);
            caller.Cancel();
            var remainingWaiter = manager.WriteStateAsync(CancellationToken.None).AsTask();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
            await remainingWaiter;
            Assert.Single(storage.Appends);
            Assert.Equal(1, state.CaptureCount);
            Assert.Equal(1, state.WriteCompletedCount);
        });
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task WaitFor(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly Channel<(SendOrPostCallback Callback, object? State)> _queue =
            Channel.CreateUnbounded<(SendOrPostCallback, object?)>();

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
                callback(state);
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }

    private sealed class LifecycleState : IStateMachine
    {
        public bool EmitEntry { get; set; } = true;
        public Action? ResetAction { get; set; }
        public Action? WriteCompletedAction { get; set; }
        public int ResetCount { get; private set; }
        public int CaptureCount { get; private set; }
        public int WriteCompletedCount { get; private set; }
        public int RecoveryCompletedCount { get; private set; }

        public void OnRecoveryCompleted() => RecoveryCompletedCount++;

        public void Reset(JournalStreamWriter writer)
        {
            ResetCount++;
            ResetAction?.Invoke();
        }

        public void WritePendingEntries(JournalStreamWriter writer)
        {
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

        public void ReplayEntry(JournalEntry entry, JournalReplayContext context) => throw new NotSupportedException();
    }
}
