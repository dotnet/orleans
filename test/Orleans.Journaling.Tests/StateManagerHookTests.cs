using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Xunit;

namespace Orleans.Journaling.Tests;

public partial class StateManagerTests
{
    [Fact]
    public async Task Hooks_ListIsLazyStableAndSupportsInPlaceDeduplication()
    {
        var sut = CreateTestSystem();
        await using var manager = sut.Manager;
        var field = typeof(JournaledStateManager).GetField("_hooks", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Null(field.GetValue(manager));
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Null(field.GetValue(manager));

        var hook = new JournaledStateHook();
        var hooks = manager.Hooks;
        hooks.Add(hook);
        if (!hooks.Contains(hook))
        {
            hooks.Add(hook);
        }
        Assert.Same(hooks, manager.Hooks);
        Assert.Same(hooks, field.GetValue(manager));
        Assert.Same(hook, Assert.Single(hooks));
        Assert.True(hooks.Remove(hook));
        Assert.Empty(manager.Hooks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hooks_DelegateOrderSurroundsCaptureAndActualAcknowledgement(bool snapshot)
    {
        var storage = new CapturingStorage { IsCompactionRequested = snapshot };
        var sut = CreateTestSystem(storage);
        await using var manager = sut.Manager;
        var state = new DurableDictionary<string, int>("state", manager, CreateDictionaryCodec<string, int>());
        List<string> events = [];
        manager.RegisterStateMachine("ack", new HookAcknowledgementState(events));
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        var expected = snapshot ? JournaledStateOperation.Snapshot : JournaledStateOperation.Write;
        CancellationToken ownedToken = default;
        manager.Hooks.Add(new JournaledStateHook
        {
            BeforeOperation = (operation, token) =>
            {
                Assert.Equal(expected, operation);
                Assert.True(token.CanBeCanceled);
                ownedToken = token;
                events.Add("before-sync");
                state["hook"] = 2;
            },
            BeforeOperationAsync = async (operation, token) =>
            {
                Assert.Equal(expected, operation);
                Assert.Equal(ownedToken, token);
                await Task.Yield();
                events.Add("before-async");
            },
            AfterOperation = (operation, token) =>
            {
                Assert.Equal(expected, operation);
                Assert.Equal(ownedToken, token);
                Assert.Single(snapshot ? storage.Replaces : storage.Appends);
                events.Add("after-sync");
            },
            AfterOperationAsync = async (_, token) =>
            {
                Assert.Equal(ownedToken, token);
                await Task.Yield();
                events.Add("after-async");
            }
        });
        state["business"] = 1;
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "before-sync", "before-async", "ack", "after-sync", "after-async" }, events);

        var replay = CreateTestSystem(storage);
        await using var replayManager = replay.Manager;
        var recovered = new DurableDictionary<string, int>("state", replayManager, CreateDictionaryCodec<string, int>());
        await replay.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        Assert.Equal(2, recovered.Count);
        Assert.Equal(1, recovered["business"]);
        Assert.Equal(2, recovered["hook"]);
    }

    [Fact]
    public async Task Hooks_CoalescedCallersShareActualOperationCallbacks()
    {
        var storage = new CapturingStorage { BlockNextAppend = true };
        var sut = CreateTestSystem(storage);
        await using var manager = sut.Manager;
        var state = new DurableDictionary<string, int>("state", manager, CreateDictionaryCodec<string, int>());
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        var before = 0;
        var after = 0;
        manager.Hooks.Add(new JournaledStateHook
        {
            BeforeOperation = (_, _) => before++,
            AfterOperation = (_, _) => after++
        });
        state["first"] = 1;
        var first = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await storage.BlockedAppendStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(1, before);
            Assert.Equal(0, after);
            state["second"] = 2;
            var second = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
            state["third"] = 3;
            var third = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
            storage.ReleaseAppend.TrySetResult();
            await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(2, before);
            Assert.Equal(2, after);
            Assert.Equal(2, storage.Appends.Count);
        }
        finally
        {
            storage.ReleaseAppend.TrySetResult();
        }
    }

    [Fact]
    public async Task Hooks_CanceledCallerRetainsPreparationCaptureAndPostCompletion()
    {
        var storage = new CapturingStorage();
        var sut = CreateTestSystem(storage);
        await using var manager = sut.Manager;
        var state = new DurableDictionary<string, int>("state", manager, CreateDictionaryCodec<string, int>());
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken ownedToken = default;
        manager.Hooks.Add(new JournaledStateHook
        {
            BeforeOperationAsync = async (_, token) =>
            {
                ownedToken = token;
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                state["scheduled"] = 2;
            },
            AfterOperation = (_, token) =>
            {
                Assert.Equal(ownedToken, token);
                Assert.False(token.IsCancellationRequested);
                Assert.Single(storage.Appends);
                completed.TrySetResult();
            }
        });
        using var caller = new CancellationTokenSource();
        state["business"] = 1;
        var write = manager.WriteStateAsync(caller.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        try
        {
            Assert.NotEqual(caller.Token, ownedToken);
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
            Assert.False(ownedToken.IsCancellationRequested);
            Assert.Empty(storage.Appends);
            Assert.False(completed.Task.IsCompleted);
            release.TrySetResult();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(2, state["scheduled"]);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task Hooks_ShutdownCancelsOwnedPreparationBeforeStorage()
    {
        var storage = new CapturingStorage();
        var sut = CreateTestSystem(storage);
        var state = new DurableDictionary<string, int>("state", sut.Manager, CreateDictionaryCodec<string, int>());
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken ownedToken = default;
        sut.Manager.Hooks.Add(new JournaledStateHook
        {
            BeforeOperationAsync = async (_, token) =>
            {
                ownedToken = token;
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        });
        state["business"] = 1;
        var write = sut.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await sut.Lifecycle.OnStop(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        Assert.True(ownedToken.IsCancellationRequested);
        Assert.Empty(storage.Appends);
        await sut.Manager.DisposeAsync();
    }

    [Fact]
    public async Task Hooks_ShutdownCallbackFailureStillDrainsOwnedPreparation()
    {
        var storage = new CapturingStorage();
        var sut = CreateTestSystem(storage);
        var manager = sut.Manager;
        var state = new DurableDictionary<string, int>("state", manager, CreateDictionaryCodec<string, int>());
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = false;
        var failure = new IOException("Cancellation callback failed.");
        manager.Hooks.Add(new JournaledStateHook
        {
            BeforeOperationAsync = async (_, token) =>
            {
                using var registration = token.Register(() => throw failure);
                entered.TrySetResult();
                await release.Task;
                drained = true;
            }
        });
        state["business"] = 1;
        var write = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        try
        {
            var shutdown = manager.DisposeAsync().AsTask();
            Assert.False(shutdown.IsCompleted);
            Assert.False(drained);
            Assert.Empty(storage.Appends);
            release.TrySetResult();
            var caught = await Assert.ThrowsAsync<AggregateException>(() =>
                shutdown.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Same(failure, Assert.Single(caught.InnerExceptions));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
            Assert.True(drained);
            Assert.Empty(storage.Appends);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task Hooks_ReentrantDisposalPreservesOwnerForExplicitRetry()
    {
        var sut = CreateTestSystem();
        await using var manager = sut.Manager;
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        manager.Hooks.Add(new JournaledStateHook
        {
            BeforeOperationAsync = async (_, _) => await manager.DisposeAsync()
        });
        var caught = await Assert.ThrowsAsync<JournaledStatePreCommitException>(() =>
            manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.Contains("same journal owner", Assert.IsType<InvalidOperationException>(caught.InnerException).Message, StringComparison.Ordinal);
        manager.Hooks.Clear();
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hooks_BeforeFailurePreservesStateForExplicitRetry(bool delete)
    {
        var storage = new CapturingStorage();
        var sut = CreateTestSystem(storage);
        await using var manager = sut.Manager;
        var state = new DurableDictionary<string, int>("state", manager, CreateDictionaryCodec<string, int>());
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        state["business"] = 1;
        var failure = new IOException("Prerequisite failed.");
        var fail = true;
        var after = 0;
        manager.Hooks.Add(new JournaledStateHook
        {
            BeforeOperation = (_, _) =>
            {
                if (fail)
                {
                    throw failure;
                }
            },
            AfterOperation = (_, _) => after++
        });
        var caught = await Assert.ThrowsAsync<JournaledStatePreCommitException>(() => InvokeAsync());
        Assert.Same(failure, caught.InnerException);
        Assert.Equal(delete ? JournaledStateOperation.Delete : JournaledStateOperation.Write, caught.Operation);
        Assert.Equal(1, state["business"]);
        Assert.Empty(storage.Appends);
        Assert.Equal(0, storage.DeleteCount);
        Assert.Equal(0, after);
        fail = false;
        await InvokeAsync();
        Assert.Equal(1, after);
        Assert.Equal(delete ? 0 : 1, state.Count);
        Assert.Equal(delete ? 1 : 0, storage.DeleteCount);
        Assert.Equal(delete ? 0 : 1, storage.Appends.Count);

        Task InvokeAsync() => (delete
            ? manager.DeleteStateAsync(TestContext.Current.CancellationToken)
            : manager.WriteStateAsync(TestContext.Current.CancellationToken)).AsTask();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hooks_AfterFailureReportsCompletedOperationAndRunsRemainingHooks(bool delete)
    {
        var storage = new CapturingStorage();
        var sut = CreateTestSystem(storage);
        await using var manager = sut.Manager;
        var state = new DurableDictionary<string, int>("state", manager, CreateDictionaryCodec<string, int>());
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        state["business"] = 1;
        var failure = new IOException("Post-persistence failure.");
        var fail = true;
        var remaining = 0;
        manager.Hooks.Add(new JournaledStateHook
        {
            AfterOperation = (_, _) =>
            {
                if (fail)
                {
                    throw failure;
                }
            }
        });
        manager.Hooks.Add(new JournaledStateHook { AfterOperation = (_, _) => remaining++ });
        var caught = await Assert.ThrowsAsync<JournaledStatePostCommitException>(() => (delete
            ? manager.DeleteStateAsync(TestContext.Current.CancellationToken)
            : manager.WriteStateAsync(TestContext.Current.CancellationToken)).AsTask());
        Assert.Same(failure, caught.InnerException);
        Assert.Equal(delete ? JournaledStateOperation.Delete : JournaledStateOperation.Write, caught.Operation);
        Assert.Equal(1, remaining);
        Assert.Equal(delete ? 0 : 1, state.Count);
        Assert.Equal(delete ? 1 : 0, storage.DeleteCount);
        Assert.Equal(delete ? 0 : 1, storage.Appends.Count);
        fail = false;
        state["later"] = 2;
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, remaining);
        Assert.Equal(2, state["later"]);
    }

    [Fact]
    public async Task Hooks_StorageFailureSkipsAfterHooksAndPreservesTerminalCause()
    {
        var failure = new IOException("Storage failed.");
        var storage = new CapturingStorage { NextAppendException = failure };
        var sut = CreateTestSystem(storage);
        await using var manager = sut.Manager;
        var state = new DurableDictionary<string, int>("state", manager, CreateDictionaryCodec<string, int>());
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        var after = 0;
        manager.Hooks.Add(new JournaledStateHook { AfterOperation = (_, _) => after++ });
        state["business"] = 1;
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask()));
        Assert.Equal(0, after);
        var fenced = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.Same(failure, fenced.InnerException);
    }

    [Fact]
    public async Task Hooks_DeleteWaitsForPrerequisitesAndRunsCompletionAfterReset()
    {
        var storage = new CapturingStorage();
        var sut = CreateTestSystem(storage);
        await using var manager = sut.Manager;
        var state = new DurableDictionary<string, int>("state", manager, CreateDictionaryCodec<string, int>());
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        state["business"] = 1;
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var after = 0;
        manager.Hooks.Add(new JournaledStateHook
        {
            BeforeOperationAsync = async (operation, token) =>
            {
                Assert.Equal(JournaledStateOperation.Delete, operation);
                Assert.True(token.CanBeCanceled);
                Assert.Equal(1, state["business"]);
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            },
            AfterOperation = (operation, _) =>
            {
                Assert.Equal(JournaledStateOperation.Delete, operation);
                Assert.Empty(state);
                Assert.Equal(1, storage.DeleteCount);
                after++;
            }
        });
        var deletion = manager.DeleteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(0, storage.DeleteCount);
            Assert.Equal(0, after);
            Assert.False(deletion.IsCompleted);
            release.TrySetResult();
            await deletion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(1, after);
            Assert.Empty(state);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task Hooks_MultipleAfterFailuresAreReportedTogether()
    {
        var sut = CreateTestSystem();
        await using var manager = sut.Manager;
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        var first = new IOException("First cleanup failed.");
        var second = new IOException("Second cleanup failed.");
        manager.Hooks.Add(new JournaledStateHook { AfterOperation = (_, _) => throw first });
        manager.Hooks.Add(new JournaledStateHook { AfterOperation = (_, _) => throw second });
        var caught = await Assert.ThrowsAsync<JournaledStatePostCommitException>(
            () => manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(new Exception[] { first, second }, Assert.IsType<AggregateException>(caught.InnerException).InnerExceptions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hooks_SameOwnerReentryIsRejectedWithoutDeadlock(bool after)
    {
        var sut = CreateTestSystem();
        await using var manager = sut.Manager;
        var state = new DurableDictionary<string, int>("state", manager, CreateDictionaryCodec<string, int>());
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        Func<JournaledStateOperation, CancellationToken, ValueTask> callback =
            async (_, token) => await manager.DeleteStateAsync(token);
        manager.Hooks.Add(new JournaledStateHook
        {
            BeforeOperationAsync = after ? null : callback,
            AfterOperationAsync = after ? callback : null
        });
        state["business"] = 1;
        var failure = await Record.ExceptionAsync(() => manager.WriteStateAsync(TestContext.Current.CancellationToken)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        var cause = after
            ? Assert.IsType<JournaledStatePostCommitException>(failure).InnerException
            : Assert.IsType<JournaledStatePreCommitException>(failure).InnerException;
        Assert.Contains("same journal owner", Assert.IsType<InvalidOperationException>(cause).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hooks_NoByteWriteStillCompletesLogicalOperation()
    {
        var storage = new CapturingStorage();
        var sut = CreateTestSystem(storage);
        await using var manager = sut.Manager;
        var state = new DurableDictionary<string, int>("state", manager, CreateDictionaryCodec<string, int>());
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        state["business"] = 1;
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        var before = 0;
        var after = 0;
        manager.Hooks.Add(new JournaledStateHook
        {
            BeforeOperation = (_, _) => before++,
            AfterOperation = (_, _) => after++
        });
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, before);
        Assert.Equal(1, after);
        Assert.Single(storage.Appends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hooks_FinalPrerequisiteCoversChangesArrivingDuringLaterOrdinaryHook(bool snapshot)
    {
        var storage = new CapturingStorage { IsCompactionRequested = snapshot };
        var sut = CreateTestSystem(storage);
        await using var manager = sut.Manager;
        var state = new DurableDictionary<string, int>("state", manager, CreateDictionaryCodec<string, int>());
        List<string> events = [];
        manager.RegisterStateMachine("ack", new HookAcknowledgementState(events));
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.Hooks.Add(new CaptureTestHook(
            (_, token) =>
            {
                Assert.True(token.CanBeCanceled);
                Assert.Equal(3, state["late"]);
                state["owner"] = 2;
                events.Add("capture-prerequisite");
                return default;
            },
            (_, _) => events.Add("capture-after")));
        manager.Hooks.Add(new JournaledStateHook
        {
            BeforeOperationAsync = async (_, token) =>
            {
                events.Add("ordinary-before");
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                events.Add("ordinary-complete");
            },
            AfterOperation = (_, _) => events.Add("ordinary-after")
        });
        state["business"] = 1;
        var write = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(new[] { "ordinary-before" }, events);
            Assert.Empty(storage.Appends);
            Assert.Empty(storage.Replaces);
            state["late"] = 3;
            release.TrySetResult();
            await write.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(new[] { "ordinary-before", "ordinary-complete", "capture-prerequisite", "ack", "capture-after", "ordinary-after" }, events);
        }
        finally
        {
            release.TrySetResult();
        }
        var replay = CreateTestSystem(storage);
        await using var recoveredManager = replay.Manager;
        var recovered = new DurableDictionary<string, int>("state", recoveredManager, CreateDictionaryCodec<string, int>());
        await replay.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        Assert.Equal(3, recovered.Count);
        Assert.Equal(1, recovered["business"]);
        Assert.Equal(2, recovered["owner"]);
        Assert.Equal(3, recovered["late"]);
    }

    [Fact]
    public async Task Hooks_DuplicateCapturePrerequisitesFailBeforeOrdinaryCallbacks()
    {
        var storage = new CapturingStorage();
        var sut = CreateTestSystem(storage);
        await using var manager = sut.Manager;
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        var ordinary = 0;
        manager.Hooks.Add(new JournaledStateHook { BeforeOperation = (_, _) => ordinary++ });
        manager.Hooks.Add(new CaptureTestHook((_, _) => default));
        var duplicate = new CaptureTestHook((_, _) => default);
        manager.Hooks.Add(duplicate);

        var failure = await Assert.ThrowsAsync<JournaledStatePreCommitException>(
            () => manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("one final capture", Assert.IsType<InvalidOperationException>(failure.InnerException).Message, StringComparison.Ordinal);
        Assert.Equal(0, ordinary);
        Assert.Empty(storage.Appends);
        manager.Hooks.Remove(duplicate);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, ordinary);
    }

    [Fact]
    public async Task Hooks_FinalPrerequisiteRejectsSameOwnerReentry()
    {
        var sut = CreateTestSystem();
        await using var manager = sut.Manager;
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        manager.Hooks.Add(new CaptureTestHook((_, token) => manager.DeleteStateAsync(token)));

        var failure = await Assert.ThrowsAsync<JournaledStatePreCommitException>(() =>
            manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.Contains("same journal owner", Assert.IsType<InvalidOperationException>(failure.InnerException).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hooks_FinalPrerequisiteRetainsOwnedCancellationAfterCallerLeaves()
    {
        var storage = new CapturingStorage();
        var sut = CreateTestSystem(storage);
        await using var manager = sut.Manager;
        var state = new DurableDictionary<string, int>("state", manager, CreateDictionaryCodec<string, int>());
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken owned = default;
        manager.Hooks.Add(new CaptureTestHook(
            async (_, token) =>
            {
                owned = token;
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                state["owner"] = 2;
            },
            (_, token) =>
            {
                Assert.Equal(owned, token);
                Assert.False(token.IsCancellationRequested);
                Assert.Single(storage.Appends);
                acknowledged.TrySetResult();
            }));
        using var caller = new CancellationTokenSource();
        state["business"] = 1;
        var write = manager.WriteStateAsync(caller.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        try
        {
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
            Assert.False(owned.IsCancellationRequested);
            Assert.Empty(storage.Appends);
            release.TrySetResult();
            await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(2, state["owner"]);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    private sealed class CaptureTestHook(
        Func<JournaledStateOperation, CancellationToken, ValueTask> before,
        Action<JournaledStateOperation, CancellationToken>? after = null) : IJournaledStateCaptureHook
    {
        public ValueTask BeforeOperationAsync(JournaledStateOperation operation, CancellationToken cancellationToken) =>
            before(operation, cancellationToken);

        public ValueTask AfterOperationAsync(JournaledStateOperation operation, CancellationToken cancellationToken)
        {
            after?.Invoke(operation, cancellationToken);
            return default;
        }
    }

    private sealed class HookAcknowledgementState(List<string> events) : IStateMachine
    {
        public void ReplayEntry(JournalEntry entry, JournalReplayContext context) { }
        public void Reset(JournalStreamWriter writer) { }
        public void WritePendingEntries(JournalStreamWriter writer) { }
        public void WriteSnapshot(JournalStreamWriter writer) { }
        public void OnWriteCompleted() => events.Add("ack");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Hooks_FailureOutcomesRoundTripWithOperationAndOriginalCause(bool after)
    {
        Exception original = after
            ? new JournaledStatePostCommitException(JournaledStateOperation.Delete, new IOException("Cleanup failed."))
            : new JournaledStatePreCommitException(JournaledStateOperation.Snapshot, new IOException("Scheduling failed."));
        var serializer = ServiceProvider.GetRequiredService<Serializer>();
        var copy = serializer.Deserialize<Exception>(serializer.SerializeToArray(original));
        if (after)
        {
            Assert.Equal(JournaledStateOperation.Delete, Assert.IsType<JournaledStatePostCommitException>(copy).Operation);
        }
        else
        {
            Assert.Equal(JournaledStateOperation.Snapshot, Assert.IsType<JournaledStatePreCommitException>(copy).Operation);
        }
        Assert.Equal(original.InnerException!.Message, Assert.IsType<IOException>(copy.InnerException).Message);
        Assert.Equal(original.Message, copy.Message);
    }
}
