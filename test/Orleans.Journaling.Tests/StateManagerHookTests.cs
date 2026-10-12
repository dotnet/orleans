using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.Diagnostics;
using Orleans.Serialization;
using Xunit;

namespace Orleans.Journaling.Tests;

public partial class StateManagerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Hooks_QueueActivityUsesSelectedOperationAndCompletesWithStorageOutcome(bool snapshot, bool storageFails)
    {
        using var parent = new Activity("journal queue test").SetIdFormat(ActivityIdFormat.W3C).Start();
        var stopped = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ActivitySources.StorageActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.ParentSpanId == parent.SpanId)
                {
                    stopped.Add(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);
        var failure = new IOException("Storage failed.");
        var storage = new CapturingStorage
        {
            IsCompactionRequested = snapshot,
            NextAppendException = storageFails && !snapshot ? failure : null,
            NextReplaceException = storageFails && snapshot ? failure : null
        };
        await using var manager = CreateTestSystem(storage).Manager;
        var state = new DurableValue<int>("state", manager, CreateValueCodec<int>());
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        state.Value = 1;
        if (storageFails)
        {
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() =>
                manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask()));
        }
        else
        {
            await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        }

        await manager.DisposeAsync();
        var activity = Assert.Single(stopped);
        var operation = snapshot ? "snapshot" : "append";
        Assert.Equal($"journal {operation}", activity.OperationName);
        Assert.Equal(operation, activity.GetTagItem(ActivityTagKeys.JournalStorageOperation));
        Assert.Equal(storageFails ? ActivityStatusCode.Error : ActivityStatusCode.Ok, activity.Status);
        Assert.Equal(storageFails ? failure.Message : null, activity.StatusDescription);
    }

    [Fact]
    public async Task Hooks_IdleRegistrationAppliesSynchronously()
    {
        await using var manager = CreateTestSystem().Manager;
        _ = new DurableValue<int>("state", manager, CreateValueCodec<int>());
        var field = typeof(JournaledStateManager).GetField("_hooks", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Null(field.GetValue(manager));

        var before = 0;
        var hook = new DelegateTestHook { BeforeOperation = (_, _) => before++ };
        Assert.True(manager.AddHookAsync(hook, cancellationToken: TestContext.Current.CancellationToken).IsCompletedSuccessfully);
        Assert.True(manager.RemoveHookAsync(hook, cancellationToken: TestContext.Current.CancellationToken).IsCompletedSuccessfully);
        await Assert.ThrowsAsync<ArgumentNullException>(() => manager.AddHookAsync(null!, cancellationToken: TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RemoveHookAsync(hook, cancellationToken: TestContext.Current.CancellationToken).AsTask());
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, before);
        Assert.True(manager.AddHookAsync(hook, index: 0, cancellationToken: TestContext.Current.CancellationToken).IsCompletedSuccessfully);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, before);
        Assert.True(manager.RemoveHookAsync(hook, cancellationToken: TestContext.Current.CancellationToken).IsCompletedSuccessfully);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, before);

        await using var withoutHooks = CreateTestSystem().Manager;
        await withoutHooks.InitializeAsync(TestContext.Current.CancellationToken);
        await withoutHooks.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Null(field.GetValue(withoutHooks));
    }

    [Fact]
    public async Task Hooks_ShutdownRejectsRegistrationAndRemoval()
    {
        var manager = CreateTestSystem().Manager;
        var hook = new DelegateTestHook();
        await manager.AddHookAsync(hook, cancellationToken: TestContext.Current.CancellationToken);

        await manager.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.AddHookAsync(hook, cancellationToken: TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.RemoveHookAsync(hook, cancellationToken: TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task Hooks_RegistrationDuringRecoveryAppliesBeforeLaterPersistence()
    {
        var entered = NewSignal();
        var release = NewSignal();
        var storage = Substitute.For<IJournalStorage>();
        storage.ReadAsync(Arg.Any<IJournalStorageConsumer>(), Arg.Any<CancellationToken>())
            .Returns(call => ReadAsync(call.Arg<IJournalStorageConsumer>(), call.Arg<CancellationToken>()));
        await using var manager = CreateTestSystem(storage).Manager;
        var before = 0;
        var after = 0;
        var hook = new DelegateTestHook
        {
            BeforeOperation = (_, _) => before++,
            AfterOperation = (_, _) => after++
        };
        var initialization = manager.InitializeAsync(TestContext.Current.CancellationToken).AsTask();
        var registration = manager.AddHookAsync(hook, cancellationToken: TestContext.Current.CancellationToken).AsTask();
        try
        {
            await WaitFor(entered.Task);
            Assert.False(initialization.IsCompleted);
            Assert.False(registration.IsCompleted);
            release.TrySetResult();
            await WaitFor(Task.WhenAll(initialization, registration));
            await manager.WriteStateAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, before);
            Assert.Equal(1, after);
        }
        finally
        {
            release.TrySetResult();
        }

        async ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken token)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            consumer.Complete(metadata: null);
        }
    }

    [Fact]
    public async Task Hooks_QueuedRegistrationReportsRecoveryFailureAndIdleRegistrationDoesNotRetryRecovery()
    {
        var entered = NewSignal();
        var release = NewSignal();
        var storage = Substitute.For<IJournalStorage>();
        var failure = new IOException("Recovery failed.");
        var fail = true;
        var reads = 0;
        storage.ReadAsync(Arg.Any<IJournalStorageConsumer>(), Arg.Any<CancellationToken>())
            .Returns(call => ReadAsync(call.Arg<IJournalStorageConsumer>(), call.Arg<CancellationToken>()));
        await using var manager = CreateTestSystem(storage).Manager;
        var initialization = manager.InitializeAsync(TestContext.Current.CancellationToken).AsTask();
        var failedBefore = 0;
        var failedRegistration = manager.AddHookAsync(new DelegateTestHook
        {
            BeforeOperation = (_, _) => failedBefore++
        }, cancellationToken: TestContext.Current.CancellationToken).AsTask();
        try
        {
            await WaitFor(entered.Task);
            release.TrySetResult();
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => WaitFor(initialization)));
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => WaitFor(failedRegistration)));
            var before = 0;
            var hook = new DelegateTestHook { BeforeOperation = (_, _) => before++ };
            Assert.True(manager.AddHookAsync(hook, cancellationToken: TestContext.Current.CancellationToken).IsCompletedSuccessfully);
            Assert.Equal(1, reads);
            fail = false;
            await manager.InitializeAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, reads);
            await manager.WriteStateAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, failedBefore);
            Assert.Equal(1, before);
        }
        finally
        {
            release.TrySetResult();
        }

        async ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken token)
        {
            reads++;
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            if (fail)
            {
                throw failure;
            }
            consumer.Complete(metadata: null);
        }
    }

    [Theory]
    [InlineData("before")]
    [InlineData("storage")]
    [InlineData("after")]
    public async Task Hooks_BusyMutationWaitsForCompleteOperationAndSurvivesCallerCancellation(string phase)
    {
        var storage = new CapturingStorage { BlockNextAppend = phase == "storage" };
        await using var manager = CreateTestSystem(storage).Manager;
        var state = new DurableValue<int>("state", manager, CreateValueCodec<int>());
        var entered = NewSignal();
        var release = NewSignal();
        var oldBefore = 0;
        var oldAfter = 0;
        var newBefore = 0;
        var newAfter = 0;
        var original = new DelegateTestHook
        {
            BeforeOperationAsync = async (_, _) =>
            {
                oldBefore++;
                if (phase == "before")
                {
                    entered.TrySetResult();
                    await release.Task;
                }
            },
            AfterOperationAsync = async (_, _) =>
            {
                oldAfter++;
                if (phase == "after")
                {
                    entered.TrySetResult();
                    await release.Task;
                }
            }
        };
        var replacement = new DelegateTestHook
        {
            BeforeOperation = (_, _) => newBefore++,
            AfterOperation = (_, _) => newAfter++
        };
        await manager.AddHookAsync(original, cancellationToken: TestContext.Current.CancellationToken);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        state.Value = 1;
        var first = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        try
        {
            await WaitFor(phase == "storage" ? storage.BlockedAppendStarted.Task : entered.Task);
            using var caller = new CancellationTokenSource();
            var added = manager.AddHookAsync(replacement, index: 0, caller.Token).AsTask();
            var removed = manager.RemoveHookAsync(original, cancellationToken: TestContext.Current.CancellationToken).AsTask();
            state.Value = 2;
            var next = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
            Assert.False(added.IsCompleted);
            Assert.False(removed.IsCompleted);
            Assert.Equal(0, newBefore);
            Assert.Equal(0, newAfter);
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => added);

            release.TrySetResult();
            storage.ReleaseAppend.TrySetResult();
            await WaitFor(Task.WhenAll(first, removed, next));
            Assert.Equal(1, oldBefore);
            Assert.Equal(1, oldAfter);
            Assert.Equal(1, newBefore);
            Assert.Equal(1, newAfter);
            Assert.Equal(2, state.Value);
        }
        finally
        {
            release.TrySetResult();
            storage.ReleaseAppend.TrySetResult();
        }
    }

    [Fact]
    public async Task Hooks_QueuedMutationSeparatesWriteCoalescingGroups()
    {
        var storage = new CapturingStorage { BlockNextAppend = true };
        await using var manager = CreateTestSystem(storage).Manager;
        var state = new LifecycleState();
        manager.RegisterStateMachine("state", state);
        var oldBefore = 0;
        var oldAfter = 0;
        var newBefore = 0;
        var newAfter = 0;
        await manager.AddHookAsync(new DelegateTestHook
        {
            BeforeOperation = (_, _) => oldBefore++,
            AfterOperation = (_, _) => oldAfter++
        }, cancellationToken: TestContext.Current.CancellationToken);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        var first = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        try
        {
            await WaitFor(storage.BlockedAppendStarted.Task);
            var beforeChange = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
            var change = manager.AddHookAsync(new DelegateTestHook
            {
                BeforeOperation = (_, _) => newBefore++,
                AfterOperation = (_, _) => newAfter++
            }, cancellationToken: TestContext.Current.CancellationToken).AsTask();
            var afterChange = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
            var coalesced = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
            storage.ReleaseAppend.TrySetResult();
            await WaitFor(Task.WhenAll(first, beforeChange, change, afterChange, coalesced));
            Assert.Equal(3, state.CaptureCount);
            Assert.Equal(3, storage.Appends.Count);
            Assert.Equal(3, oldBefore);
            Assert.Equal(3, oldAfter);
            Assert.Equal(1, newBefore);
            Assert.Equal(1, newAfter);
        }
        finally
        {
            storage.ReleaseAppend.TrySetResult();
        }
    }

    [Fact]
    public async Task Hooks_ResetCanQueueRegistrationWithoutChangingCurrentAfterHooks()
    {
        var storage = new CapturingStorage();
        await using var manager = CreateTestSystem(storage).Manager;
        var state = new LifecycleState();
        Task? registration = null;
        var newBefore = 0;
        var newAfter = 0;
        state.ResetAction = () =>
        {
            if (state.ResetCount == 2)
            {
                registration = manager.AddHookAsync(new DelegateTestHook
                {
                    BeforeOperation = (_, _) => newBefore++,
                    AfterOperation = (_, _) => newAfter++
                }, cancellationToken: TestContext.Current.CancellationToken).AsTask();
            }
        };
        manager.RegisterStateMachine("state", state);
        await manager.AddHookAsync(new DelegateTestHook
        {
            AfterOperation = (operation, _) =>
            {
                if (operation == JournaledStateOperation.Delete)
                {
                    Assert.NotNull(registration);
                    Assert.False(registration.IsCompleted);
                    Assert.Equal(0, newBefore);
                    Assert.Equal(0, newAfter);
                }
            }
        }, cancellationToken: TestContext.Current.CancellationToken);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        await manager.DeleteStateAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(registration);
        await WaitFor(registration);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, newBefore);
        Assert.Equal(1, newAfter);
    }

    [Fact]
    public async Task Hooks_FailedQueuedMutationReportsErrorAndLeavesOwnerUsable()
    {
        var storage = new CapturingStorage { BlockNextAppend = true };
        await using var manager = CreateTestSystem(storage).Manager;
        manager.RegisterStateMachine("state", new LifecycleState());
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        var first = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        try
        {
            await WaitFor(storage.BlockedAppendStarted.Task);
            var invalid = manager.AddHookAsync(new DelegateTestHook(), index: 1, cancellationToken: TestContext.Current.CancellationToken).AsTask();
            var before = 0;
            var valid = new DelegateTestHook { BeforeOperation = (_, _) => before++ };
            var accepted = manager.AddHookAsync(valid, cancellationToken: TestContext.Current.CancellationToken).AsTask();
            storage.ReleaseAppend.TrySetResult();
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => WaitFor(invalid));
            await WaitFor(Task.WhenAll(first, accepted));
            await manager.WriteStateAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, before);
        }
        finally
        {
            storage.ReleaseAppend.TrySetResult();
        }
    }

    [Fact]
    public async Task Hooks_StorageFailureFaultsQueuedRegistrationWithOriginalCause()
    {
        var failure = new IOException("Storage failed.");
        var storage = new CapturingStorage { BlockNextAppend = true, NextAppendException = failure };
        await using var manager = CreateTestSystem(storage).Manager;
        manager.RegisterStateMachine("state", new LifecycleState());
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        var write = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        try
        {
            await WaitFor(storage.BlockedAppendStarted.Task);
            var registration = manager.AddHookAsync(new DelegateTestHook(), cancellationToken: TestContext.Current.CancellationToken).AsTask();
            storage.ReleaseAppend.TrySetResult();
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => WaitFor(write)));
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => WaitFor(registration)));
            var fenced = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.AddHookAsync(new DelegateTestHook(), cancellationToken: TestContext.Current.CancellationToken).AsTask());
            Assert.Same(failure, fenced.InnerException);
        }
        finally
        {
            storage.ReleaseAppend.TrySetResult();
        }
    }

    [Fact]
    public async Task Hooks_ShutdownCancelsQueuedMutationWithoutChangingCommittedOperation()
    {
        var storage = new CapturingStorage();
        await using var manager = CreateTestSystem(storage).Manager;
        manager.RegisterStateMachine("state", new LifecycleState());
        var entered = NewSignal();
        var release = NewSignal();
        var original = new DelegateTestHook
        {
            AfterOperationAsync = async (_, _) =>
            {
                entered.TrySetResult();
                await release.Task;
            }
        };
        await manager.AddHookAsync(original, cancellationToken: TestContext.Current.CancellationToken);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        var write = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        try
        {
            await WaitFor(entered.Task);
            var added = manager.AddHookAsync(new DelegateTestHook(), cancellationToken: TestContext.Current.CancellationToken).AsTask();
            var removed = manager.RemoveHookAsync(original, cancellationToken: TestContext.Current.CancellationToken).AsTask();
            var shutdown = manager.DisposeAsync().AsTask();
            Assert.False(shutdown.IsCompleted);
            release.TrySetResult();
            await WaitFor(Task.WhenAll(write, shutdown));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WaitFor(added));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WaitFor(removed));
            Assert.Single(storage.Appends);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hooks_DelegateOrderSurroundsCaptureAndActualAcknowledgement(bool snapshot)
    {
        var storage = new CapturingStorage { IsCompactionRequested = snapshot, BlockNextAppend = !snapshot, BlockNextReplace = snapshot };
        await using var manager = CreateTestSystem(storage).Manager;
        var state = new DurableDictionary<string, int>("state", manager, CreateDictionaryCodec<string, int>());
        List<string> events = [];
        manager.RegisterStateMachine("ack", new HookAcknowledgementState(events));
        var expected = snapshot ? JournaledStateOperation.Snapshot : JournaledStateOperation.Write;
        CancellationToken ownedToken = default;
        await manager.AddHookAsync(new DelegateTestHook
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
        }, cancellationToken: TestContext.Current.CancellationToken);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        state["business"] = 1;
        var write = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        try
        {
            await (snapshot ? storage.ReplaceEntered : storage.BlockedAppendStarted).Task
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(new[] { "before-sync", "before-async", "capture" }, events);
            Assert.False(write.IsCompleted);
            (snapshot ? storage.ReleaseReplace : storage.ReleaseAppend).TrySetResult();
            await write.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(new[] { "before-sync", "before-async", "capture", "ack", "after-sync", "after-async" }, events);
        }
        finally
        {
            (snapshot ? storage.ReleaseReplace : storage.ReleaseAppend).TrySetResult();
        }

        await using var replayManager = CreateTestSystem(storage).Manager;
        var recovered = new DurableDictionary<string, int>("state", replayManager, CreateDictionaryCodec<string, int>());
        await replayManager.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, recovered.Count);
        Assert.Equal(1, recovered["business"]);
        Assert.Equal(2, recovered["hook"]);
    }

    [Theory]
    [InlineData(JournaledStateOperation.Write)]
    [InlineData(JournaledStateOperation.Snapshot)]
    [InlineData(JournaledStateOperation.Delete)]
    public async Task Hooks_BeforeFailurePreservesStateForExplicitRetry(JournaledStateOperation operation)
    {
        var storage = new CapturingStorage { IsCompactionRequested = operation == JournaledStateOperation.Snapshot };
        await using var manager = CreateTestSystem(storage).Manager;
        var state = new DurableDictionary<string, int>("state", manager, CreateDictionaryCodec<string, int>());
        var capture = new AlwaysWritingState();
        manager.RegisterStateMachine("capture", capture);
        var failure = new IOException("Prerequisite failed.");
        var fail = true;
        var after = 0;
        await manager.AddHookAsync(new DelegateTestHook
        {
            BeforeOperation = (_, _) =>
            {
                if (fail)
                {
                    throw failure;
                }
            },
            AfterOperation = (_, _) => after++
        }, cancellationToken: TestContext.Current.CancellationToken);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        state["business"] = 1;
        var pendingBytes = manager.PendingWriteByteCount;
        var caught = await Assert.ThrowsAsync<JournaledStatePreCommitException>(() => InvokeAsync());
        Assert.Same(failure, caught.InnerException);
        Assert.Contains($"'{operation}'", caught.Message, StringComparison.Ordinal);
        Assert.Equal(1, state["business"]);
        Assert.Equal(pendingBytes, manager.PendingWriteByteCount);
        Assert.Equal(0, capture.AppendEntriesCount);
        Assert.Equal(0, capture.WriteCompletedCount);
        Assert.Empty(storage.Appends);
        Assert.Empty(storage.Replaces);
        Assert.Equal(0, storage.DeleteCount);
        Assert.Equal(0, after);
        storage.IsCompactionRequested = false;
        fail = false;
        await InvokeAsync();
        Assert.Equal(1, after);
        Assert.Equal(operation == JournaledStateOperation.Delete ? 0 : 1, state.Count);
        Assert.Equal(operation == JournaledStateOperation.Delete ? 1 : 0, storage.DeleteCount);
        Assert.Equal(operation == JournaledStateOperation.Write ? 1 : 0, storage.Appends.Count);
        Assert.Equal(operation == JournaledStateOperation.Snapshot ? 1 : 0, storage.Replaces.Count);

        Task InvokeAsync() => (operation == JournaledStateOperation.Delete
            ? manager.DeleteStateAsync(TestContext.Current.CancellationToken)
            : manager.WriteStateAsync(TestContext.Current.CancellationToken)).AsTask();
    }

    [Theory]
    [InlineData(JournaledStateOperation.Write)]
    [InlineData(JournaledStateOperation.Snapshot)]
    [InlineData(JournaledStateOperation.Delete)]
    public async Task Hooks_AfterFailureReportsCompletedOperationAndRunsRemainingHooks(JournaledStateOperation operation)
    {
        var storage = new CapturingStorage { IsCompactionRequested = operation == JournaledStateOperation.Snapshot };
        await using var manager = CreateTestSystem(storage).Manager;
        var state = new DurableDictionary<string, int>("state", manager, CreateDictionaryCodec<string, int>());
        List<string> events = [];
        manager.RegisterStateMachine("ack", new HookAcknowledgementState(events));
        var first = new IOException("First cleanup failed.");
        var second = new IOException("Second cleanup failed.");
        var fail = true;
        await manager.AddHookAsync(new DelegateTestHook
        {
            AfterOperation = (_, _) =>
            {
                if (fail)
                {
                    throw first;
                }
            }
        }, cancellationToken: TestContext.Current.CancellationToken);
        await manager.AddHookAsync(new DelegateTestHook
        {
            AfterOperationAsync = async (_, _) =>
            {
                await Task.Yield();
                events.Add("second");
                if (fail)
                {
                    throw second;
                }
            }
        }, cancellationToken: TestContext.Current.CancellationToken);
        await manager.AddHookAsync(new DelegateTestHook { AfterOperation = (_, _) => events.Add("last") }, cancellationToken: TestContext.Current.CancellationToken);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        state["business"] = 1;
        var caught = await Assert.ThrowsAsync<JournaledStatePostCommitException>(() => (operation == JournaledStateOperation.Delete
            ? manager.DeleteStateAsync(TestContext.Current.CancellationToken)
            : manager.WriteStateAsync(TestContext.Current.CancellationToken)).AsTask());
        Assert.Contains($"'{operation}'", caught.Message, StringComparison.Ordinal);
        Assert.Equal(new Exception[] { first, second }, Assert.IsType<AggregateException>(caught.InnerException).InnerExceptions);
        Assert.Equal(operation == JournaledStateOperation.Delete
            ? new[] { "second", "last" }
            : new[] { "capture", "ack", "second", "last" }, events);
        Assert.Equal(operation == JournaledStateOperation.Delete ? 0 : 1, state.Count);
        Assert.Equal(operation == JournaledStateOperation.Delete ? 1 : 0, storage.DeleteCount);
        fail = false;

        await using var replayManager = CreateTestSystem(storage).Manager;
        var recovered = new DurableDictionary<string, int>("state", replayManager, CreateDictionaryCodec<string, int>());
        await replayManager.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(operation == JournaledStateOperation.Delete ? 0 : 1, recovered.Count);
        if (operation != JournaledStateOperation.Delete)
        {
            Assert.Equal(1, recovered["business"]);
        }

        state["later"] = 2;
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, state["later"]);
    }

    [Fact]
    public async Task Hooks_StorageFailureSkipsAfterHooksAndPreservesTerminalCause()
    {
        var failure = new IOException("Storage failed.");
        var storage = new CapturingStorage { NextAppendException = failure };
        await using var manager = CreateTestSystem(storage).Manager;
        var state = new DurableDictionary<string, int>("state", manager, CreateDictionaryCodec<string, int>());
        var after = 0;
        await manager.AddHookAsync(new DelegateTestHook { AfterOperation = (_, _) => after++ }, cancellationToken: TestContext.Current.CancellationToken);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        state["business"] = 1;
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask()));
        Assert.Equal(0, after);
        var fenced = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.Same(failure, fenced.InnerException);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hooks_FinalPrerequisiteCoversChangesArrivingDuringLaterOrdinaryHook(bool snapshot)
    {
        var storage = new CapturingStorage { IsCompactionRequested = snapshot };
        await using var manager = CreateTestSystem(storage).Manager;
        var state = new DurableDictionary<string, int>("state", manager, CreateDictionaryCodec<string, int>());
        List<string> events = [];
        manager.RegisterStateMachine("ack", new HookAcknowledgementState(events));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await manager.AddHookAsync(new DelegateTestHook(
            (_, _) =>
            {
                Assert.Equal(3, state["late"]);
                state["owner"] = 2;
                events.Add("capture-prerequisite");
                return default;
            },
            (_, _) => events.Add("capture-after")), cancellationToken: TestContext.Current.CancellationToken);
        await manager.AddHookAsync(new DelegateTestHook
        {
            BeforeOperationAsync = async (_, token) =>
            {
                events.Add("ordinary-before");
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                events.Add("ordinary-complete");
            },
            AfterOperation = (_, _) => events.Add("ordinary-after")
        }, cancellationToken: TestContext.Current.CancellationToken);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        state["business"] = 1;
        var write = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(new[] { "ordinary-before" }, events);
            Assert.Empty(storage.Appends);
            Assert.Empty(storage.Replaces);
            state["late"] = 3;
            release.TrySetResult();
            await write.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(new[] { "ordinary-before", "ordinary-complete", "capture-prerequisite", "capture", "ack", "capture-after", "ordinary-after" }, events);
        }
        finally
        {
            release.TrySetResult();
        }

        await using var recoveredManager = CreateTestSystem(storage).Manager;
        var recovered = new DurableDictionary<string, int>("state", recoveredManager, CreateDictionaryCodec<string, int>());
        await recoveredManager.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, recovered.Count);
        Assert.Equal(1, recovered["business"]);
        Assert.Equal(2, recovered["owner"]);
        Assert.Equal(3, recovered["late"]);
    }

    [Fact]
    public async Task Hooks_CanceledCallerRetainsFinalPreparationAndPostCompletion()
    {
        var storage = new CapturingStorage();
        await using var manager = CreateTestSystem(storage).Manager;
        var state = new DurableDictionary<string, int>("state", manager, CreateDictionaryCodec<string, int>());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken ownedToken = default;
        Func<JournaledStateOperation, CancellationToken, ValueTask> before = async (_, token) =>
        {
            ownedToken = token;
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            state["scheduled"] = 2;
        };
        Action<JournaledStateOperation, CancellationToken> after = (_, token) =>
        {
            Assert.Equal(ownedToken, token);
            Assert.False(token.IsCancellationRequested);
            Assert.Single(storage.Appends);
            completed.TrySetResult();
        };
        await manager.AddHookAsync(new DelegateTestHook(before, after), cancellationToken: TestContext.Current.CancellationToken);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        using var caller = new CancellationTokenSource();
        state["business"] = 1;
        var write = manager.WriteStateAsync(caller.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.NotEqual(caller.Token, ownedToken);
            state["late"] = 3;
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
            Assert.False(ownedToken.IsCancellationRequested);
            Assert.Empty(storage.Appends);
            Assert.False(completed.Task.IsCompleted);
            release.TrySetResult();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(2, state["scheduled"]);
            await using var recoveredManager = CreateTestSystem(storage).Manager;
            var recovered = new DurableDictionary<string, int>("state", recoveredManager, CreateDictionaryCodec<string, int>());
            await recoveredManager.InitializeAsync(TestContext.Current.CancellationToken);
            Assert.Equal(3, recovered.Count);
            Assert.Equal(1, recovered["business"]);
            Assert.Equal(2, recovered["scheduled"]);
            Assert.Equal(3, recovered["late"]);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hooks_SynchronousFinalPrerequisiteAndCaptureShareContinuationAfterAsyncEarlierHook(bool snapshot)
    {
        var scheduler = new QueuedHookTaskScheduler();
        await scheduler.Run(async () =>
        {
            var storage = new CapturingStorage { IsCompactionRequested = snapshot };
            await using var manager = CreateTestSystem(storage).Manager;
            var state = new DurableDictionary<string, int>("state", manager, CreateDictionaryCodec<string, int>());
            List<string> events = [];
            List<string> prepared = [];
            manager.RegisterStateMachine("ack", new HookAcknowledgementState(events));
            var queueMutation = true;
            Task? mutation = null;
            await manager.AddHookAsync(new DelegateTestHook
            {
                BeforeOperation = (_, _) =>
                {
                    events.Add("final-before");
                    prepared.AddRange(state.Keys);
                    if (queueMutation)
                    {
                        Assert.Empty(state);
                        queueMutation = false;
                        mutation = scheduler.Queue(() =>
                        {
                            state["intent"] = 1;
                            events.Add("queued-mutation");
                        });
                    }
                }
            }, cancellationToken: TestContext.Current.CancellationToken);
            await manager.AddHookAsync(new DelegateTestHook
            {
                BeforeOperationAsync = async (_, _) =>
                {
                    events.Add("ordinary-before");
                    await Task.Yield();
                    events.Add("ordinary-complete");
                }
            }, cancellationToken: TestContext.Current.CancellationToken);
            await manager.InitializeAsync(TestContext.Current.CancellationToken);
            await manager.WriteStateAsync(TestContext.Current.CancellationToken);

            Assert.NotNull(mutation);
            await mutation;
            Assert.Empty(prepared);
            Assert.Equal(1, state["intent"]);
            await using (var recoveredManager = CreateTestSystem(storage).Manager)
            {
                var recovered = new DurableDictionary<string, int>("state", recoveredManager, CreateDictionaryCodec<string, int>());
                await recoveredManager.InitializeAsync(TestContext.Current.CancellationToken);
                Assert.Empty(recovered);
            }

            Assert.Equal(
                ["ordinary-before", "ordinary-complete", "final-before", "capture", "ack", "queued-mutation"],
                events);
            await manager.WriteStateAsync(TestContext.Current.CancellationToken);
            Assert.Equal(["intent"], prepared);
            await using var replayManager = CreateTestSystem(storage).Manager;
            var replayed = new DurableDictionary<string, int>("state", replayManager, CreateDictionaryCodec<string, int>());
            await replayManager.InitializeAsync(TestContext.Current.CancellationToken);
            var intent = Assert.Single(replayed);
            Assert.Equal("intent", intent.Key);
            Assert.Equal(1, intent.Value);
        });
    }

    [Fact]
    public async Task Hooks_BeforeCallbacksRunInReverseAndAfterCallbacksRunInListOrder()
    {
        await using var manager = CreateTestSystem().Manager;
        List<string> events = [];
        manager.RegisterStateMachine("ack", new HookAcknowledgementState(events));
        await manager.AddHookAsync(new DelegateTestHook
        {
            BeforeOperation = (_, _) => events.Add("second-before"),
            AfterOperation = (_, _) => events.Add("second-after")
        }, cancellationToken: TestContext.Current.CancellationToken);
        await manager.AddHookAsync(new DelegateTestHook
        {
            BeforeOperation = (_, _) => events.Add("third-before"),
            AfterOperation = (_, _) => events.Add("third-after")
        }, cancellationToken: TestContext.Current.CancellationToken);
        await manager.AddHookAsync(new DelegateTestHook
        {
            BeforeOperation = (_, _) => events.Add("first-before"),
            AfterOperation = (_, _) => events.Add("first-after")
        }, index: 0, cancellationToken: TestContext.Current.CancellationToken);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            ["third-before", "second-before", "first-before", "capture", "ack", "first-after", "second-after", "third-after"],
            events);
    }

    [Fact]
    public async Task Hooks_DeleteWaitsForPrerequisitesAndCompletesAfterReset()
    {
        var storage = new CapturingStorage();
        await using var manager = CreateTestSystem(storage).Manager;
        var state = new DurableValue<int>("state", manager, CreateValueCodec<int>());
        var deleting = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var before = 0;
        var after = 0;
        await manager.AddHookAsync(new DelegateTestHook(
            async (operation, token) =>
            {
                before++;
                if (!deleting)
                {
                    Assert.Equal(JournaledStateOperation.Write, operation);
                    return;
                }

                Assert.Equal(JournaledStateOperation.Delete, operation);
                Assert.Equal(1, state.Value);
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            },
            (operation, _) =>
            {
                if (!deleting)
                {
                    return;
                }

                Assert.Equal(JournaledStateOperation.Delete, operation);
                Assert.Equal(0, state.Value);
                Assert.Equal(1, storage.DeleteCount);
                after++;
            }), cancellationToken: TestContext.Current.CancellationToken);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        state.Value = 1;
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        deleting = true;
        var deletion = manager.DeleteStateAsync(TestContext.Current.CancellationToken).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(0, storage.DeleteCount);
            Assert.Equal(0, after);
            Assert.False(deletion.IsCompleted);
            release.TrySetResult();
            await deletion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(1, after);
            deleting = false;
            await manager.WriteStateAsync(TestContext.Current.CancellationToken);
            Assert.Equal(3, before);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task Hooks_ShutdownCallbackFailureStillDrainsOwnedPreparation()
    {
        var storage = new CapturingStorage();
        var manager = CreateTestSystem(storage).Manager;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = false;
        var failure = new IOException("Cancellation callback failed.");
        var hook = new DelegateTestHook
        {
            BeforeOperationAsync = async (_, token) =>
            {
                using var registration = token.Register(() => throw failure);
                entered.TrySetResult();
                await release.Task;
                drained = true;
            }
        };
        await manager.AddHookAsync(hook, cancellationToken: TestContext.Current.CancellationToken);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        var write = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var queued = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
            var shutdown = manager.DisposeAsync().AsTask();
            var concurrentShutdown = manager.DisposeAsync().AsTask();
            Assert.Same(shutdown, concurrentShutdown);
            Assert.False(shutdown.IsCompleted);
            Assert.False(drained);
            release.TrySetResult();
            var caught = await Assert.ThrowsAsync<AggregateException>(() =>
                shutdown.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Same(failure, Assert.Single(caught.InnerExceptions));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            Assert.True(drained);
            Assert.Empty(storage.Appends);
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                manager.RemoveHookAsync(hook, cancellationToken: TestContext.Current.CancellationToken).AsTask());
        }
        finally
        {
            release.TrySetResult();
            await Assert.ThrowsAsync<AggregateException>(() => manager.DisposeAsync().AsTask());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hooks_ShutdownCancelsQueuedInitializationAfterOwnedCallbackDrains(bool after)
    {
        var storage = new CapturingStorage();
        var manager = CreateTestSystem(storage).Manager;
        var state = new LifecycleState();
        manager.RegisterStateMachine("state", state);
        var entered = NewSignal();
        var release = NewSignal();
        Func<JournaledStateOperation, CancellationToken, ValueTask> callback = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        await manager.AddHookAsync(new DelegateTestHook
        {
            BeforeOperationAsync = after ? null : callback,
            AfterOperationAsync = after ? callback : null
        }, cancellationToken: TestContext.Current.CancellationToken);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        var write = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        try
        {
            await WaitFor(entered.Task);
            var initializing = manager.InitializeAsync(TestContext.Current.CancellationToken).AsTask();
            var deleting = manager.DeleteStateAsync(TestContext.Current.CancellationToken).AsTask();
            var shutdown = manager.DisposeAsync().AsTask();
            Assert.False(shutdown.IsCompleted);
            Assert.False(initializing.IsCompleted);
            Assert.False(deleting.IsCompleted);
            release.TrySetResult();
            await WaitFor(shutdown);
            if (after)
            {
                await WaitFor(write);
            }
            else
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WaitFor(write));
            }

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WaitFor(initializing));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WaitFor(deleting));
            Assert.True(initializing.IsCanceled);
            Assert.True(deleting.IsCanceled);
            Assert.Equal(after ? 1 : 0, state.CaptureCount);
            Assert.Equal(after ? 1 : 0, state.WriteCompletedCount);
            Assert.Equal(after ? 1 : 0, storage.Appends.Count);
            Assert.Equal(0, storage.DeleteCount);
        }
        finally
        {
            release.TrySetResult();
            await manager.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Hooks_ShutdownCallbackFailureDrainsActualStorageAndPreservesItsOutcome(bool snapshot, bool storageFails)
    {
        var entered = NewSignal();
        var release = NewSignal();
        var cancellationFailure = new IOException("Cancellation callback failed.");
        var storageFailure = new IOException("Actual storage failure.");
        var completed = false;
        var storage = Substitute.For<IJournalStorage>();
        storage.IsCompactionRequested.Returns(snapshot);
        storage.ReadAsync(Arg.Any<IJournalStorageConsumer>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<IJournalStorageConsumer>().Complete(metadata: null);
            return ValueTask.CompletedTask;
        });
        storage.AppendAsync(Arg.Any<ReadOnlySequence<byte>>(), Arg.Any<CancellationToken>())
            .Returns(call => WriteAsync(call.Arg<ReadOnlySequence<byte>>(), call.Arg<CancellationToken>()));
        storage.ReplaceAsync(Arg.Any<ReadOnlySequence<byte>>(), Arg.Any<CancellationToken>())
            .Returns(call => WriteAsync(call.Arg<ReadOnlySequence<byte>>(), call.Arg<CancellationToken>()));
        var manager = CreateTestSystem(storage).Manager;
        var state = new LifecycleState();
        manager.RegisterStateMachine("state", state);
        CancellationTokenRegistration registration = default;
        var after = 0;
        await manager.AddHookAsync(new DelegateTestHook
        {
            BeforeOperation = (_, token) => registration = token.Register(() => throw cancellationFailure),
            AfterOperation = (_, _) => after++
        }, cancellationToken: TestContext.Current.CancellationToken);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        var write = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        try
        {
            await WaitFor(entered.Task);
            var queued = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
            var shutdown = manager.DisposeAsync().AsTask();
            Assert.Same(shutdown, manager.DisposeAsync().AsTask());
            Assert.False(shutdown.IsCompleted);
            Assert.False(completed);
            Assert.Equal(0, state.WriteCompletedCount);
            Assert.Equal(0, after);
            release.TrySetResult();
            var caught = await Assert.ThrowsAsync<AggregateException>(() => WaitFor(shutdown));
            Assert.Same(cancellationFailure, Assert.Single(caught.InnerExceptions));
            Assert.True(completed);
            if (storageFails)
            {
                Assert.Same(storageFailure, await Assert.ThrowsAsync<IOException>(() => WaitFor(write)));
                Assert.Same(storageFailure, await Assert.ThrowsAsync<IOException>(() => WaitFor(queued)));
            }
            else
            {
                await WaitFor(write);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WaitFor(queued));
            }

            Assert.Equal(storageFails ? 0 : 1, state.WriteCompletedCount);
            Assert.Equal(storageFails ? 0 : 1, after);
        }
        finally
        {
            release.TrySetResult();
            registration.Dispose();
            await Assert.ThrowsAsync<AggregateException>(() => manager.DisposeAsync().AsTask());
        }

        async ValueTask WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token)
        {
            var original = bytes.ToArray();
            entered.TrySetResult();
            await release.Task;
            Assert.True(token.IsCancellationRequested);
            Assert.Equal(original, bytes.ToArray());
            completed = true;
            if (storageFails)
            {
                throw storageFailure;
            }
        }
    }

    [Fact]
    public async Task Hooks_ShutdownCallbackReentrySharesDisposalCompletion()
    {
        var storage = new CapturingStorage();
        var manager = CreateTestSystem(storage).Manager;
        var state = new DurableValue<int>("state", manager, CreateValueCodec<int>());
        var entered = NewSignal();
        var release = NewSignal();
        Task? reenteredDisposal = null;
        var callbackCount = 0;
        await manager.AddHookAsync(new DelegateTestHook
        {
            AfterOperationAsync = async (_, token) =>
            {
                using var registration = token.Register(() =>
                {
                    callbackCount++;
                    reenteredDisposal = manager.DisposeAsync().AsTask();
                });
                entered.TrySetResult();
                await release.Task;
            }
        }, cancellationToken: TestContext.Current.CancellationToken);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        state.Value = 1;
        var write = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        try
        {
            await WaitFor(entered.Task);
            var disposal = manager.DisposeAsync().AsTask();
            Assert.Equal(1, callbackCount);
            Assert.Same(disposal, reenteredDisposal);
            Assert.False(disposal.IsCompleted);
            release.TrySetResult();
            await WaitFor(Task.WhenAll(write, disposal));
            Assert.Same(disposal, manager.DisposeAsync().AsTask());
            Assert.Single(storage.Appends);
        }
        finally
        {
            release.TrySetResult();
            await manager.DisposeAsync();
        }
    }

    [Fact]
    public async Task Hooks_ShutdownDrainsEveryAfterHookDespiteCleanupFailures()
    {
        var storage = new CapturingStorage();
        var manager = CreateTestSystem(storage).Manager;
        var state = new DurableValue<int>("state", manager, CreateValueCodec<int>());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new IOException("First cleanup failed.");
        var second = new IOException("Second cleanup failed.");
        List<string> events = [];
        await manager.AddHookAsync(new DelegateTestHook
        {
            AfterOperationAsync = async (_, token) =>
            {
                Assert.Single(storage.Appends);
                entered.TrySetResult();
                await release.Task;
                Assert.True(token.IsCancellationRequested);
                events.Add("first");
                throw first;
            }
        }, cancellationToken: TestContext.Current.CancellationToken);
        await manager.AddHookAsync(new DelegateTestHook { AfterOperation = (_, _) => throw second }, cancellationToken: TestContext.Current.CancellationToken);
        await manager.AddHookAsync(new DelegateTestHook { AfterOperation = (_, _) => events.Add("last") }, cancellationToken: TestContext.Current.CancellationToken);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        state.Value = 1;
        var write = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var shutdown = manager.DisposeAsync().AsTask();
            var concurrentShutdown = manager.DisposeAsync().AsTask();
            Assert.Same(shutdown, concurrentShutdown);
            Assert.False(shutdown.IsCompleted);
            release.TrySetResult();
            var caught = await Assert.ThrowsAsync<JournaledStatePostCommitException>(() =>
                write.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Equal(new Exception[] { first, second }, Assert.IsType<AggregateException>(caught.InnerException).InnerExceptions);
            await shutdown.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(new[] { "first", "last" }, events);
            Assert.Single(storage.Appends);
        }
        finally
        {
            release.TrySetResult();
            await manager.DisposeAsync();
        }
    }

    [Fact]
    public async Task Hooks_QueuedWritesCoalesceAndRetainSnapshotRequirement()
    {
        var context = new QueuedSynchronizationContext();
        await context.Run(async () =>
        {
            var storage = new CapturingStorage();
            await using var manager = CreateTestSystem(storage).Manager;
            var state = new DurableValue<int>("state", manager, CreateValueCodec<int>());
            List<JournaledStateOperation> before = [];
            List<JournaledStateOperation> after = [];
            await manager.AddHookAsync(new DelegateTestHook
            {
                BeforeOperation = (operation, _) => before.Add(operation),
                AfterOperation = (operation, _) => after.Add(operation)
            }, cancellationToken: TestContext.Current.CancellationToken);
            await manager.InitializeAsync(TestContext.Current.CancellationToken);
            state.Value = 1;
            var append = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
            storage.IsCompactionRequested = true;
            state.Value = 2;
            var snapshot = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
            storage.IsCompactionRequested = false;
            state.Value = 3;
            var anotherAppend = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();

            Assert.Empty(storage.Appends);
            Assert.Empty(storage.Replaces);
            Assert.Empty(before);
            await Task.WhenAll(append, snapshot, anotherAppend);
            Assert.Equal([JournaledStateOperation.Snapshot], before);
            Assert.Equal([JournaledStateOperation.Snapshot], after);
            Assert.Empty(storage.Appends);
            Assert.Single(storage.Replaces);

            await using var recoveredManager = CreateTestSystem(storage).Manager;
            var recovered = new DurableValue<int>("state", recoveredManager, CreateValueCodec<int>());
            await recoveredManager.InitializeAsync(TestContext.Current.CancellationToken);
            Assert.Equal(3, recovered.Value);
        });
    }

    [Fact]
    public async Task Hooks_BeforeFailureRestoresSnapshotForAlreadyQueuedWrite()
    {
        var storage = new CapturingStorage { IsCompactionRequested = true };
        await using var manager = CreateTestSystem(storage).Manager;
        var state = new DurableValue<int>("state", manager, CreateValueCodec<int>());
        var entered = NewSignal();
        var release = NewSignal();
        var failure = new IOException("Snapshot prerequisite failed.");
        List<JournaledStateOperation> before = [];
        List<JournaledStateOperation> after = [];
        await manager.AddHookAsync(new DelegateTestHook
        {
            BeforeOperationAsync = async (operation, _) =>
            {
                before.Add(operation);
                if (before.Count == 1)
                {
                    entered.TrySetResult();
                    await release.Task;
                    throw failure;
                }
            },
            AfterOperation = (operation, _) => after.Add(operation)
        }, cancellationToken: TestContext.Current.CancellationToken);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        state.Value = 1;
        var first = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        try
        {
            await WaitFor(entered.Task);
            storage.IsCompactionRequested = false;
            state.Value = 2;
            var retry = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
            Assert.Empty(storage.Appends);
            Assert.Empty(storage.Replaces);
            release.TrySetResult();
            var caught = await Assert.ThrowsAsync<JournaledStatePreCommitException>(() => WaitFor(first));
            Assert.Same(failure, caught.InnerException);
            await WaitFor(retry);
            Assert.Equal([JournaledStateOperation.Snapshot, JournaledStateOperation.Snapshot], before);
            Assert.Equal([JournaledStateOperation.Snapshot], after);
            Assert.Empty(storage.Appends);
            Assert.Single(storage.Replaces);
            await using var recoveredManager = CreateTestSystem(storage).Manager;
            var recovered = new DurableValue<int>("state", recoveredManager, CreateValueCodec<int>());
            await recoveredManager.InitializeAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, recovered.Value);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task Hooks_SnapshotAcknowledgementPreservesLaterQueuedSnapshot()
    {
        var storage = new CapturingStorage { IsCompactionRequested = true, BlockNextReplace = true };
        await using var manager = CreateTestSystem(storage).Manager;
        var state = new DurableValue<int>("state", manager, CreateValueCodec<int>());
        List<JournaledStateOperation> before = [];
        List<JournaledStateOperation> after = [];
        await manager.AddHookAsync(new DelegateTestHook
        {
            BeforeOperation = (operation, _) => before.Add(operation),
            AfterOperation = (operation, _) => after.Add(operation)
        }, cancellationToken: TestContext.Current.CancellationToken);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        state.Value = 1;
        var first = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        try
        {
            await WaitFor(storage.ReplaceEntered.Task);
            state.Value = 2;
            var second = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
            storage.IsCompactionRequested = false;
            Assert.Empty(storage.Replaces);
            Assert.Equal([JournaledStateOperation.Snapshot], before);
            Assert.Empty(after);
            storage.ReleaseReplace.TrySetResult();
            await WaitFor(Task.WhenAll(first, second));
            Assert.Equal([JournaledStateOperation.Snapshot, JournaledStateOperation.Snapshot], before);
            Assert.Equal([JournaledStateOperation.Snapshot, JournaledStateOperation.Snapshot], after);
            Assert.Empty(storage.Appends);
            Assert.Equal(2, storage.Replaces.Count);
            await using var recoveredManager = CreateTestSystem(storage).Manager;
            var recovered = new DurableValue<int>("state", recoveredManager, CreateValueCodec<int>());
            await recoveredManager.InitializeAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, recovered.Value);
        }
        finally
        {
            storage.ReleaseReplace.TrySetResult();
        }
    }

    [Fact]
    public async Task Hooks_CoalescedCallersShareActualOperationCallbacks()
    {
        var storage = new CapturingStorage { BlockNextAppend = true };
        await using var manager = CreateTestSystem(storage).Manager;
        var state = new DurableDictionary<string, int>("state", manager, CreateDictionaryCodec<string, int>());
        var before = 0;
        var after = 0;
        await manager.AddHookAsync(new DelegateTestHook { BeforeOperation = (_, _) => before++, AfterOperation = (_, _) => after++ }, cancellationToken: TestContext.Current.CancellationToken);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        state["first"] = 1;
        var first = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        try
        {
            await storage.BlockedAppendStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
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
    public async Task Hooks_StateChangesDuringAfterCallbackPersistInNextOperation()
    {
        var storage = new CapturingStorage();
        await using var manager = CreateTestSystem(storage).Manager;
        var state = new DurableDictionary<string, int>("state", manager, CreateDictionaryCodec<string, int>());
        var entered = NewSignal();
        var release = NewSignal();
        var after = 0;
        await manager.AddHookAsync(new DelegateTestHook
        {
            AfterOperationAsync = async (_, _) =>
            {
                if (++after == 1)
                {
                    entered.TrySetResult();
                    await release.Task;
                }
            }
        }, cancellationToken: TestContext.Current.CancellationToken);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        state["first"] = 1;
        var first = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        try
        {
            await WaitFor(entered.Task);
            Assert.Single(storage.Appends);
            Assert.False(first.IsCompleted);
            state["later"] = 2;
            var next = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
            Assert.False(next.IsCompleted);

            await using (var beforeNext = CreateTestSystem(storage).Manager)
            {
                var recovered = new DurableDictionary<string, int>("state", beforeNext, CreateDictionaryCodec<string, int>());
                await beforeNext.InitializeAsync(TestContext.Current.CancellationToken);
                Assert.Equal(1, Assert.Single(recovered).Value);
            }

            release.TrySetResult();
            await WaitFor(Task.WhenAll(first, next));
            Assert.Equal(2, after);
            Assert.Equal(2, storage.Appends.Count);
            await using var afterNext = CreateTestSystem(storage).Manager;
            var persisted = new DurableDictionary<string, int>("state", afterNext, CreateDictionaryCodec<string, int>());
            await afterNext.InitializeAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, persisted.Count);
            Assert.Equal(1, persisted["first"]);
            Assert.Equal(2, persisted["later"]);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task Hooks_NoByteWriteStillCompletesLogicalOperation()
    {
        var storage = new CapturingStorage();
        await using var manager = CreateTestSystem(storage).Manager;
        var state = new DurableValue<int>("state", manager, CreateValueCodec<int>());
        var before = 0;
        var after = 0;
        await manager.AddHookAsync(new DelegateTestHook { BeforeOperation = (_, _) => before++, AfterOperation = (_, _) => after++ }, cancellationToken: TestContext.Current.CancellationToken);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        state.Value = 1;
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, before);
        Assert.Equal(1, after);
        before = 0;
        after = 0;
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, before);
        Assert.Equal(1, after);
        Assert.Single(storage.Appends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Hooks_FailureOutcomesRoundTripWithTypeAndOriginalCause(bool after)
    {
        Exception original = after
            ? new JournaledStatePostCommitException(JournaledStateOperation.Delete, new IOException("Cleanup failed."))
            : new JournaledStatePreCommitException(JournaledStateOperation.Snapshot, new IOException("Scheduling failed."));
        var serializer = ServiceProvider.GetRequiredService<Serializer>();
        var copy = serializer.Deserialize<Exception>(serializer.SerializeToArray(original));
        if (after)
        {
            Assert.IsType<JournaledStatePostCommitException>(copy);
        }
        else
        {
            Assert.IsType<JournaledStatePreCommitException>(copy);
        }

        Assert.Equal(original.InnerException!.Message, Assert.IsType<IOException>(copy.InnerException).Message);
        Assert.Equal(original.Message, copy.Message);
    }

    private sealed class QueuedHookTaskScheduler : TaskScheduler
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

    private sealed class DelegateTestHook(
        Func<JournaledStateOperation, CancellationToken, ValueTask>? before = null,
        Action<JournaledStateOperation, CancellationToken>? after = null) : IJournaledStateHook
    {
        public Action<JournaledStateOperation, CancellationToken>? BeforeOperation { get; init; }
        public Func<JournaledStateOperation, CancellationToken, ValueTask>? BeforeOperationAsync { get; init; } = before;
        public Action<JournaledStateOperation, CancellationToken>? AfterOperation { get; init; } = after;
        public Func<JournaledStateOperation, CancellationToken, ValueTask>? AfterOperationAsync { get; init; }

        ValueTask IJournaledStateHook.BeforeOperationAsync(JournaledStateOperation operation, CancellationToken cancellationToken)
        {
            BeforeOperation?.Invoke(operation, cancellationToken);
            return BeforeOperationAsync?.Invoke(operation, cancellationToken) ?? default;
        }

        ValueTask IJournaledStateHook.AfterOperationAsync(JournaledStateOperation operation, CancellationToken cancellationToken)
        {
            AfterOperation?.Invoke(operation, cancellationToken);
            return AfterOperationAsync?.Invoke(operation, cancellationToken) ?? default;
        }
    }

    private sealed class HookAcknowledgementState(List<string> events) : IStateMachine
    {
        public void ReplayEntry(JournalEntry entry, JournalReplayContext context) { }
        public void Reset(JournalStreamWriter writer) { }
        public void WritePendingEntries(JournalStreamWriter writer) => events.Add("capture");
        public void WriteSnapshot(JournalStreamWriter writer) => events.Add("capture");
        public void OnWriteCompleted() => events.Add("ack");
    }
}
