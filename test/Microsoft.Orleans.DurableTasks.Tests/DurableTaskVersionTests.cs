using System.Collections.Concurrent;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Diagnostics;
using Orleans.Messaging;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>
/// Verifies which version of a change <see cref="DurableTask.GetVersion(string, int, int)"/> returns to executions which
/// started before, during, and after a deployment, and how executions on unsupported versions stall.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class DurableTaskVersionTests
{
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public Task FirstRunRecordsTheNewestVersionAndReplayReturnsIt() => ActivationScheduler.RunAsync(async () =>
    {
        var workflowId = TaskId.CreateRoot("version-first-run");
        await using var harness = new DurableTaskReplayHarness(grain => Workflow(grain, maxSupported: 2));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(workflowId, TestCancellation);
            await CommitAtGateAsync(activation);

            var record = Assert.Single(activation.Storage.Tasks, task => task.Id == workflowId.Child("$version:change"));
            Assert.Equal(2, record.State.Result!.GetResult<int>());
        }

        // A later deployment adds version 3. The execution keeps the version which it recorded.
        harness.Workflow = grain => Workflow(grain, maxSupported: 3);
        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        replay.Grain.OpenGate();

        Assert.Equal(2, (await replay.GetResponseAsync(workflowId, TestCancellation)).GetResult<int>());

        static async DurableTask<int> Workflow(ReplayGrain grain, int maxSupported)
        {
            var version = DurableTask.GetVersion("change", 0, maxSupported);
            await DurableTask.Run(static _ => { }).WithId("first");
            await grain.WaitAtGateAsync();
            return version;
        }
    });

    [Fact]
    public Task ExecutionWhichEarlierCodeRanPastTheCheckFollowsVersionZero() => ActivationScheduler.RunAsync(async () =>
    {
        var workflowId = TaskId.CreateRoot("version-old-execution");
        await using var harness = new DurableTaskReplayHarness(static grain => Before(grain));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(workflowId, TestCancellation);
            await CommitAtGateAsync(activation);
        }

        harness.Workflow = static grain => After(grain);
        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        replay.Grain.OpenGate();

        // The earlier run recorded "second" after the point where the check now stands, so the execution follows the
        // code before the change, and records that decision.
        Assert.Equal("0:second", (await replay.GetResponseAsync(workflowId, TestCancellation)).GetResult<string>());
        var record = Assert.Single(replay.Storage.Tasks, task => task.Id == workflowId.Child("$version:change"));
        Assert.Equal(0, record.State.Result!.GetResult<int>());

        static async DurableTask<string> Before(ReplayGrain grain)
        {
            await DurableTask.Run(static _ => { }).WithId("first");
            await DurableTask.Run(static _ => { }).WithId("second");
            await grain.WaitAtGateAsync();
            return "unchanged";
        }

        static async DurableTask<string> After(ReplayGrain grain)
        {
            await DurableTask.Run(static _ => { }).WithId("first");
            var version = DurableTask.GetVersion("change", 0, 1);
            var step = version >= 1 ? "replacement" : "second";
            await DurableTask.Run(static _ => { }).WithId(step);
            await grain.WaitAtGateAsync();
            return $"{version}:{step}";
        }
    });

    [Fact]
    public Task ExecutionWhichEarlierCodeDidNotRunPastTheCheckFollowsTheNewestVersion() => ActivationScheduler.RunAsync(async () =>
    {
        var workflowId = TaskId.CreateRoot("version-not-past");
        await using var harness = new DurableTaskReplayHarness(static grain => Before(grain));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(workflowId, TestCancellation);
            await CommitAtGateAsync(activation);
        }

        harness.Workflow = static grain => After(grain);
        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        replay.Grain.OpenGate();

        // The earlier run waited at the gate before the point where the check now stands.
        Assert.Equal("1:replacement", (await replay.GetResponseAsync(workflowId, TestCancellation)).GetResult<string>());

        static async DurableTask<string> Before(ReplayGrain grain)
        {
            await DurableTask.Run(static _ => { }).WithId("first");
            await grain.WaitAtGateAsync();
            await DurableTask.Run(static _ => { }).WithId("second");
            return "unchanged";
        }

        static async DurableTask<string> After(ReplayGrain grain)
        {
            await DurableTask.Run(static _ => { }).WithId("first");
            await grain.WaitAtGateAsync();
            var version = DurableTask.GetVersion("change", 0, 1);
            var step = version >= 1 ? "replacement" : "second";
            await DurableTask.Run(static _ => { }).WithId(step);
            return $"{version}:{step}";
        }
    });

    [Fact]
    public Task OperationInsertedBeforeTheCheckKeepsTheExecutionOnVersionZero() => ActivationScheduler.RunAsync(async () =>
    {
        var workflowId = TaskId.CreateRoot("version-inserted");
        bool? replayingAtCheck = null;
        await using var harness = new DurableTaskReplayHarness(static grain => Before(grain));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(workflowId, TestCancellation);
            await CommitAtGateAsync(activation);
        }

        harness.Workflow = grain => After(grain, replaying => replayingAtCheck = replaying);
        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        replay.Grain.OpenGate();

        // The inserted step ended the replay, and "second" still shows that the earlier run went past the check.
        Assert.Equal("0:second", (await replay.GetResponseAsync(workflowId, TestCancellation)).GetResult<string>());
        Assert.False(replayingAtCheck);

        static async DurableTask<string> Before(ReplayGrain grain)
        {
            await DurableTask.Run(static _ => { }).WithId("first");
            await DurableTask.Run(static _ => { }).WithId("second");
            await grain.WaitAtGateAsync();
            return "unchanged";
        }

        static async DurableTask<string> After(ReplayGrain grain, Action<bool> observe)
        {
            await DurableTask.Run(static _ => { }).WithId("first");
            await DurableTask.Run(static _ => { }).WithId("inserted");
            observe(DurableTask.Current.IsReplaying);
            var version = DurableTask.GetVersion("change", 0, 1);
            var step = version >= 1 ? "replacement" : "second";
            await DurableTask.Run(static _ => { }).WithId(step);
            await grain.WaitAtGateAsync();
            return $"{version}:{step}";
        }
    });

    [Fact]
    public Task EveryCheckOfAChangeInAnExecutionReturnsTheFirstDecision() => ActivationScheduler.RunAsync(async () =>
    {
        var workflowId = TaskId.CreateRoot("version-loop");
        var versions = new ConcurrentQueue<int>();
        await using var harness = new DurableTaskReplayHarness(grain => Loop(grain, versions, check: false));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(workflowId, TestCancellation);
            await CommitAtGateAsync(activation);
        }

        // The earlier run completed two iterations before the gate. Later iterations, which run after the replay, still
        // follow the version which the first iteration decided.
        harness.Workflow = grain => Loop(grain, versions, check: true);
        await using (var replay = await harness.ActivateAsync(TestCancellation))
        {
            await CommitAtGateAsync(replay);
        }

        Assert.Equal([0, 0, 0, 0], versions);

        // A further replay reads the recorded decision.
        versions.Clear();
        await using var recovered = await harness.ActivateAsync(TestCancellation);
        await recovered.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        recovered.Grain.OpenGate();
        Assert.Equal(4, (await recovered.GetResponseAsync(workflowId, TestCancellation)).GetResult<int>());
        Assert.Equal([0, 0, 0, 0], versions);

        static async DurableTask<int> Loop(ReplayGrain grain, ConcurrentQueue<int> versions, bool check)
        {
            var iterations = check ? 4 : 2;
            for (var i = 0; i < iterations; i++)
            {
                if (check)
                {
                    versions.Enqueue(DurableTask.GetVersion("loop", 0, 1));
                }

                await DurableTask.Run(static _ => { }).WithId("iteration");
            }

            await grain.WaitAtGateAsync();
            return iterations;
        }
    });

    [Fact]
    public Task RemovedCheckLeavesNoOperationForReplaysToReach() => ActivationScheduler.RunAsync(async () =>
    {
        var workflowId = TaskId.CreateRoot("version-removed");
        var observations = new ConcurrentQueue<(bool Replaying, int Version)>();
        await using var harness = new DurableTaskReplayHarness(static grain => Before(grain));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(workflowId, TestCancellation);
            await CommitAtGateAsync(activation);
        }

        // A deployment removed the check of "retired", whose record stays in the execution's tree.
        harness.Workflow = grain => After(grain, observations);
        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        replay.Grain.OpenGate();
        await replay.GetResponseAsync(workflowId, TestCancellation);

        var (replaying, version) = Assert.Single(observations);
        Assert.False(replaying);
        Assert.Equal(1, version);

        static async DurableTask Before(ReplayGrain grain)
        {
            DurableTask.GetVersion("retired", 0, 1);
            await DurableTask.Run(static _ => { }).WithId("first");
            await grain.WaitAtGateAsync();
        }

        static async DurableTask After(ReplayGrain grain, ConcurrentQueue<(bool, int)> observations)
        {
            await DurableTask.Run(static _ => { }).WithId("first");
            observations.Enqueue((DurableTask.Current.IsReplaying, DurableTask.GetVersion("added", 0, 1)));
            await grain.WaitAtGateAsync();
        }
    });

    [Fact]
    public Task StepInheritsTheVersionWhichItsParentRecorded() => ActivationScheduler.RunAsync(async () =>
    {
        var workflowId = TaskId.CreateRoot("version-step");
        await using var harness = new DurableTaskReplayHarness(static _ => Workflow());
        await using var activation = await harness.ActivateAsync(TestCancellation);
        await activation.ScheduleAsync(workflowId, TestCancellation);

        Assert.Equal((1, 1, 2), (await activation.GetResponseAsync(workflowId, TestCancellation)).GetResult<(int, int, int)>());

        // The step which inherited the parent's decision records none of its own; the step which found none records it.
        var records = activation.Storage.Tasks
            .Select(static task => task.Id.ToString())
            .Where(static id => id.Contains("/$version:", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);
        Assert.Equal(["version-step/$version:change", "version-step/other/$version:other"], records);

        static async DurableTask<(int, int, int)> Workflow()
        {
            var parent = DurableTask.GetVersion("change", 0, 1);
            var inherited = await DurableTask.Run(static _ => DurableTask.GetVersion("change", 0, 2)).WithId("inherits");
            var own = await DurableTask.Run(static _ => DurableTask.GetVersion("other", 0, 2)).WithId("other");
            return (parent, inherited, own);
        }
    });

    [Fact]
    public Task UnsupportedVersionStallsTheExecutionUntilCodeWhichSupportsItReplaysIt() => ActivationScheduler.RunAsync(async () =>
    {
        var workflowId = TaskId.CreateRoot("version-stall");
        var stalled = new TaskCompletionSource<DurableTaskVersionException>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = DurableTaskEvents.AllEvents.Subscribe(new EventObserver(evt =>
        {
            if (evt is DurableTaskEvents.ExecutionStalled { Exception: var exception } && evt.TaskId == workflowId)
            {
                stalled.TrySetResult(exception);
            }
        }));

        await using var harness = new DurableTaskReplayHarness(grain => Workflow(grain, maxSupported: 2));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(workflowId, TestCancellation);
            await CommitAtGateAsync(activation);
        }

        // Code which supports only version 1, such as an earlier build during a rolling deployment, replays the task.
        harness.Workflow = grain => Workflow(grain, maxSupported: 1);
        await using (var older = await harness.ActivateAsync(TestCancellation))
        {
            var exception = await stalled.Task.WaitAsync(Timeout, TestCancellation);
            Assert.Equal(("change", 2, 0, 1), (exception.Name, exception.Version, exception.MinSupported, exception.MaxSupported));

            // The task stays pending, its attempt ended, and the grain keeps a slow wake-up for a later activation.
            Assert.True(older.Storage.TryGetTask(workflowId, out var state));
            Assert.False(state.Result is { IsCompleted: true });
            await WaitUntilAsync(() => ((IInboxWorkSource)older.Runtime).GetWakeupDelay() == TimeSpan.FromMinutes(1));
        }

        harness.Workflow = grain => Workflow(grain, maxSupported: 2);
        await using var newer = await harness.ActivateAsync(TestCancellation);
        await newer.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        newer.Grain.OpenGate();
        Assert.Equal(2, (await newer.GetResponseAsync(workflowId, TestCancellation)).GetResult<int>());

        static async DurableTask<int> Workflow(ReplayGrain grain, int maxSupported)
        {
            await DurableTask.Run(static _ => { }).WithId("first");
            var version = DurableTask.GetVersion("change", 0, maxSupported);
            await grain.WaitAtGateAsync();
            return version;
        }
    });

    [Fact]
    public Task StalledAttemptWhichHandlesItsCancellationChangesNothingAndKeepsItsTaskPending() => ActivationScheduler.RunAsync(async () =>
    {
        var workflowId = TaskId.CreateRoot("version-stall-handled");
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failures = new ConcurrentQueue<Exception>();
        using var subscription = DurableTaskEvents.AllEvents.Subscribe(new EventObserver(evt =>
        {
            if (evt is DurableTaskEvents.ExecutionStalled && evt.TaskId == workflowId)
            {
                stalled.TrySetResult();
            }
        }));

        await using var harness = new DurableTaskReplayHarness(static grain => Recorded(grain));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(workflowId, TestCancellation);
            await CommitAtGateAsync(activation);
        }

        // Code which raised the minimum supported version replays an execution which recorded version 0. The check, the
        // step which the body starts after catching the cancellation, and its state change all report the attempt's
        // cancellation for the unsupported version.
        harness.Workflow = grain => Handling(grain, failures.Enqueue);
        await using var replay = await harness.ActivateAsync(TestCancellation);
        await stalled.Task.WaitAsync(Timeout, TestCancellation);

        Assert.Equal(3, failures.Count);
        Assert.All(failures, static failure =>
        {
            var canceled = Assert.IsType<DurableTaskAttemptCanceledException>(failure);
            Assert.Equal(DurableTaskAttemptCancellationReason.UnsupportedVersion, canceled.Reason);
            var unsupported = Assert.IsType<DurableTaskVersionException>(canceled.InnerException);
            Assert.Equal(("change", 0, 1, 1), (unsupported.Name, unsupported.Version, unsupported.MinSupported, unsupported.MaxSupported));
        });
        Assert.Equal(0, replay.Grain.Counter.Value);
        Assert.True(replay.Storage.TryGetTask(workflowId, out var state));
        Assert.False(state.Result is { IsCompleted: true });
        Assert.DoesNotContain(replay.Storage.Tasks, task => task.Id == workflowId.Child("fallback"));

        static async DurableTask<string> Recorded(ReplayGrain grain)
        {
            DurableTask.GetVersion("change", 0, 0);
            await DurableTask.Run(static _ => { }).WithId("first");
            await grain.WaitAtGateAsync();
            return "recorded";
        }

        static async DurableTask<string> Handling(ReplayGrain grain, Action<Exception> observe)
        {
            try
            {
                DurableTask.GetVersion("change", 1, 1);
            }
            catch (DurableTaskAttemptCanceledException exception)
            {
                observe(exception);
                try
                {
                    await DurableTask.Run(static _ => { }).WithId("fallback");
                }
                catch (Exception scheduleFailure)
                {
                    observe(scheduleFailure);
                }

                try
                {
                    grain.Counter.Value++;
                }
                catch (Exception stateFailure)
                {
                    observe(stateFailure);
                }
            }

            return "handled";
        }
    });

    [Fact]
    public Task DescriptorFollowsTheVersionWhichTheExecutionRecorded() => ActivationScheduler.RunAsync(async () =>
    {
        var workflowId = TaskId.CreateRoot("version-descriptor");
        await using var harness = new DurableTaskReplayHarness(static grain => Workflow(grain, Original));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(workflowId, TestCancellation);
            await CommitAtGateAsync(activation);
        }

        // The next deployment adds version 2; the pending execution keeps version 1.
        harness.Workflow = static grain => Workflow(grain, Extended);
        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        replay.Grain.OpenGate();

        Assert.Equal("1:True:False", (await replay.GetResponseAsync(workflowId, TestCancellation)).GetResult<string>());

        static async DurableTask<string> Workflow(ReplayGrain grain, DurableVersion change)
        {
            var version = change.Get();
            await grain.WaitAtGateAsync();
            return $"{version}:{change.IsAtLeast(1)}:{change.IsAtLeast(2)}";
        }
    });

    private static readonly DurableVersion Original = new("descriptor", minSupported: 0, maxSupported: 1);

    private static readonly DurableVersion Extended = new("descriptor", minSupported: 0, maxSupported: 2);

    /// <summary>Waits until the workflow waits at the gate, then commits what it staged with an unrelated write.</summary>
    private static async Task CommitAtGateAsync(ReplayActivation activation)
    {
        await activation.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        activation.Grain.RequestUnrelatedWrite();
        await activation.Grain.UnrelatedWrites;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition wasn't met in time.");
            await Task.Delay(10, TestCancellation);
        }
    }
}
