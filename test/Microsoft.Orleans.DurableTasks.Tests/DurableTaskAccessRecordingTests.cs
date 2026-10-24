using NSubstitute;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Orleans.Journaling;
using Orleans.Messaging;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>
/// Verifies which accesses a durable task body's records hold: the body's own, and none of the runtime's work which the
/// body's operations request.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class DurableTaskAccessRecordingTests
{
    private static readonly TaskId WorkflowId = TaskId.CreateRoot("workflow");
    private static readonly DurableSignal<int> Ready = new("ready");
    private static readonly DurableSignal<int> Next = new("next");
    private static readonly DurableSignal<int> End = new("end");

    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    private static TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public static TheoryData<string, string> RuntimeOperations()
    {
        var data = new TheoryData<string, string>();
        foreach (var operation in new[] { "post", "schedule", "await-child", "child-accesses", "delay", "handle-cancel", "handle-abort", "manager-cancel", "manager-abort", "signal-wait", "when-all", "when-any", "get-version", "run-inline" })
        {
            data.Add(operation, DurableTaskJournalHarness.Json);
            data.Add(operation, DurableTaskJournalHarness.OrleansBinary);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RuntimeOperations))]
    public Task RuntimeOperationFromABodyRecordsOnlyTheBodysAccesses(string operation, string journalFormatKey) => ActivationScheduler.RunAsync(async () =>
    {
        IDurableTaskManager? manager = null;
        await using var harness = new DurableTaskReplayHarness(grain => Workflow(grain, manager!, operation), journalFormatKey);
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            manager = activation.Runtime;
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
            activation.Grain.RequestUnrelatedWrite();
            await activation.Grain.UnrelatedWrites;

            // The body's log holds its read and its write of the counter, around the operation, and nothing of the work
            // which the operation requested from the runtime.
            var counter = new JournaledStateAccessTarget("counter", Substitute.For<IJournaledStateManager>()).Id;
            Assert.Equal(
                [
                    (JournaledStateAccessKind.Read, JournaledStateOperation.ValueGet, counter),
                    (JournaledStateAccessKind.Write, JournaledStateOperation.ValueSet, counter),
                ],
                ParseHeaders(activation.Storage.GetAccessLog(WorkflowId).Span));
            Assert.Equal(1, activation.Grain.Counter.Value);
        }

        // The replay returns both records, so the increment applies once and the body completes without diverging.
        await using var replay = await harness.ActivateAsync(TestCancellation);
        manager = replay.Runtime;
        await replay.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        replay.Grain.OpenGate();
        Assert.Equal(0, (await replay.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<int>());
        Assert.Equal(1, replay.Grain.Counter.Value);

        static async DurableTask<int> Workflow(ReplayGrain grain, IDurableTaskManager manager, string operation)
        {
            var value = grain.Counter.Value;
            await RunAsync(grain, manager, operation);
            grain.Counter.Value = value + 1;
            await grain.WaitAtGateAsync();
            return value;
        }
    });

    private static async Task RunAsync(ReplayGrain grain, IDurableTaskManager manager, string operation)
    {
        var sibling = TaskId.CreateRoot("sibling");
        switch (operation)
        {
            case "post":
                _ = DurableTask.Run(static _ => { }).WithId("child").Post();
                break;
            case "schedule":
                await (await DurableTask.Run(static _ => { }).WithId("child").ScheduleAsync());
                break;
            case "await-child":
                await DurableTask.Run(static _ => { }).WithId("child");
                break;
            case "child-accesses":
                await DurableTask.Run(_ => grain.Log.Append(grain.Log.Count)).WithId("child");
                break;
            case "delay":
                _ = await DurableTask.Delay(TimeSpan.FromHours(1)).WithId("delay").ScheduleAsync();
                break;
            case "handle-cancel":
                var canceled = await DurableTask.Delay(TimeSpan.FromHours(1)).WithId("delay").ScheduleAsync();
                await canceled.CancelAsync();
                break;
            case "handle-abort":
                var aborted = await DurableTask.Delay(TimeSpan.FromHours(1)).WithId("delay").ScheduleAsync();
                await aborted.AbortAsync("not needed");
                break;
            case "manager-cancel":
                // The manager cancels a task which this grain retains, such as the body's own child.
                var managed = await DurableTask.Delay(TimeSpan.FromHours(1)).WithId("delay").ScheduleAsync();
                await manager.CancelAsync(managed.Id, "not needed");
                break;
            case "manager-abort":
                await manager.GetTask<int>(sibling).AbortAsync("not needed");
                break;
            case "signal-wait":
                await manager.SignalAsync(WorkflowId, Ready, 1);
                _ = await Ready.WaitAsync();
                break;
            case "when-all":
                await DurableTask.WhenAll([DurableTask.Run(static _ => { }), DurableTask.Run(static _ => { })]);
                break;
            case "when-any":
                await DurableTask.WhenAny([DurableTask.Run(static _ => { }), DurableTask.Run(static _ => { })]);
                break;
            case "get-version":
                _ = DurableTask.GetVersion("change", 0, 1);
                break;
            case "run-inline":
                // The effect's record covers its accesses, so the body's log holds none of them.
                DurableTask.RunInline("effect", () => grain.Log.Append(grain.Log.Count));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
        }
    }

    [Theory]
    [MemberData(nameof(DurableTaskJournalHarness.Formats), MemberType = typeof(DurableTaskJournalHarness))]
    public Task EffectWithinAStateOperationRunsAsPartOfItAndShiftsNoIdentifier(string journalFormatKey) => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(static grain => Workflow(grain), journalFormatKey);
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await CommitAtGateAsync(activation);
            Assert.Equal([7, 7], activation.Grain.List);
        }

        // The replay returns AddRange's record without enumerating its source, so the nested effects don't run, and the
        // unnamed effect and step after it keep the identifiers which the first run gave them.
        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        replay.Grain.OpenGate();
        Assert.Equal(52, (await replay.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<int>());
        Assert.Equal([7, 7], replay.Grain.List);
        Assert.Equal(2, harness.EffectRuns);

        static async DurableTask<int> Workflow(ReplayGrain grain)
        {
            grain.List.AddRange(Enumerable.Range(0, 2).Select(_ => DurableTask.RunInline(() =>
            {
                grain.Harness.CountEffectRun();
                return 7;
            })));
            var effect = DurableTask.RunInline(static () => 42);
            var step = await DurableTask.Run(static _ => 10);
            await grain.WaitAtGateAsync();
            return effect + step;
        }
    });

    [Theory]
    [InlineData("state-operation", DurableTaskJournalHarness.Json)]
    [InlineData("state-operation", DurableTaskJournalHarness.OrleansBinary)]
    [InlineData("effect", DurableTaskJournalHarness.Json)]
    [InlineData("effect", DurableTaskJournalHarness.OrleansBinary)]
    public Task DurableOperationWithinACoveredRegionThrowsAndReplaysTheSameOutcome(string region, string journalFormatKey) => ActivationScheduler.RunAsync(async () =>
    {
        string? firstOutcome = null;
        await using var harness = new DurableTaskReplayHarness(grain => Workflow(grain, region, outcome => firstOutcome ??= outcome), journalFormatKey);
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await CommitAtGateAsync(activation);
            Assert.Contains("(ORLEANS0227)", firstOutcome, StringComparison.Ordinal);
        }

        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        replay.Grain.OpenGate();
        Assert.Equal(firstOutcome, (await replay.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<string>());
        Assert.False(replay.Storage.TryGetTask(WorkflowId.Child("posted"), out _));

        static async DurableTask<string> Workflow(ReplayGrain grain, string region, Action<string> observe)
        {
            string outcome;
            try
            {
                if (region == "effect")
                {
                    DurableTask.RunInline("effect", static () => { _ = DurableTask.Run(static _ => { }).WithId("posted").Post(); });
                }
                else
                {
                    grain.List.AddRange(Post());
                }

                outcome = "posted";
            }
            catch (InvalidOperationException exception)
            {
                outcome = exception.Message;
            }

            observe(outcome);
            await DurableTask.Run(static _ => { }).WithId("after");
            await grain.WaitAtGateAsync();
            return outcome;
        }

        static IEnumerable<int> Post()
        {
            _ = DurableTask.Run(static _ => { }).WithId("posted").Post();
            yield return 1;
        }
    });

    [Theory]
    [MemberData(nameof(DurableTaskJournalHarness.Formats), MemberType = typeof(DurableTaskJournalHarness))]
    public Task CoveredFlowRunsItsAwaitsUnrecordedAndRejectsDurableOperations(string journalFormatKey) => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(static grain => Workflow(grain), journalFormatKey);
        await using var activation = await harness.ActivateAsync(TestCancellation);
        await activation.ScheduleAsync(WorkflowId, TestCancellation);
        await activation.Grain.AtGate.WaitAsync(Timeout, TestCancellation);

        // The covered body's increment and effect ran directly: the attempt's log holds only the body's own read after it.
        var counter = new JournaledStateAccessTarget("counter", Substitute.For<IJournaledStateManager>()).Id;
        Assert.Equal(1, activation.Grain.Counter.Value);
        Assert.Equal([(JournaledStateAccessKind.Read, JournaledStateOperation.ValueGet, counter)], ParseHeaders(activation.Storage.GetAccessLog(WorkflowId).Span));
        activation.Grain.OpenGate();
        Assert.Equal("1 7 rejected", (await activation.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<string>());

        static async DurableTask<string> Workflow(ReplayGrain grain)
        {
            var attempt = (GrainDurableTaskContext)DurableTask.Current;
            var (effect, rejected) = await attempt.RunCoveredAsync(async () =>
            {
                await Task.Yield();
                grain.Counter.Value++;
                var effect = DurableTask.RunInline(static () => 7);
                var rejected = Record.Exception(static () => DurableTask.Run(static _ => { }).WithId("covered").Post()) is InvalidOperationException;
                return (effect, rejected);
            });

            var value = grain.Counter.Value;
            await DurableTask.Run(static _ => { }).WithId("after");
            await grain.WaitAtGateAsync();
            return $"{value} {effect} {(rejected ? "rejected" : "posted")}";
        }
    });

    private static async Task CommitAtGateAsync(ReplayActivation activation)
    {
        await activation.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        activation.Grain.RequestUnrelatedWrite();
        await activation.Grain.UnrelatedWrites;
    }

    [Theory]
    [MemberData(nameof(DurableTaskJournalHarness.Formats), MemberType = typeof(DurableTaskJournalHarness))]
    public Task ReplayWhichPassesOtherArgumentsDiverges(string journalFormatKey) => ActivationScheduler.RunAsync(async () =>
    {
        var key = "apple";
        var stalled = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = global::Orleans.DurableTasks.Diagnostics.DurableTaskEvents.AllEvents.Subscribe(new EventObserver(evt =>
        {
            if (evt is global::Orleans.DurableTasks.Diagnostics.DurableTaskEvents.ExecutionStalled { Reason: DurableTaskAttemptCancellationReason.Diverged } stall && evt.TaskId == WorkflowId)
            {
                stalled.TrySetResult(stall.Exception);
            }
        }));

        await using var harness = new DurableTaskReplayHarness(grain => Workflow(grain, key), journalFormatKey);
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await CommitAtGateAsync(activation);
        }

        // A later version reads another key with the same operation: its record holds the first key's arguments.
        key = "pear";
        await using (var diverged = await harness.ActivateAsync(TestCancellation))
        {
            var divergence = Assert.IsType<DurableTaskDivergenceException>(await stalled.Task.WaitAsync(Timeout, TestCancellation));
            Assert.Contains("was recorded with other arguments", divergence.Message, StringComparison.Ordinal);
            Assert.Contains("DictionaryTryGetValue", divergence.Message, StringComparison.Ordinal);
        }

        // Code which passes the recorded arguments replays the task.
        key = "apple";
        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        replay.Grain.OpenGate();
        Assert.False((await replay.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<bool>());

        static async DurableTask<bool> Workflow(ReplayGrain grain, string key)
        {
            var found = grain.Stock.TryGetValue(key, out _);
            await DurableTask.Run(static _ => { }).WithId("step");
            await grain.WaitAtGateAsync();
            return found;
        }
    });

    [Fact]
    public void ArgumentHashesAreStableAcrossProcessesAndDistinguishArguments()
    {
        // Every process and runtime version computes these values, so records which one process staged replay in another.
        Assert.Equal(unchecked((int)0x485AC4B1u), Hash("apple"));
        Assert.Equal(unchecked((int)0x20369D19u), Hash(42));
        Assert.Equal(unchecked((int)0x92284EADu), Hash((7, "x")));
        Assert.Equal(unchecked((int)0x49AFACA6u), Hash(new Guid("6f9619ff-8b86-d011-b42d-00cf4fc964ff")));
        Assert.Equal(unchecked((int)0x2D934FBEu), Hash(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)));
        Assert.Equal(unchecked((int)0x0044ADF2u), Hash(DayOfWeek.Friday));
        Assert.Equal(unchecked((int)0x00CC8A3Cu), Hash(1.5));
        Assert.Equal(unchecked((int)0x050C5D1Fu), Hash<string?>(null));

        // Equal arguments hash alike, whatever their integer width; other arguments differ.
        Assert.Equal(Hash(42), Hash(42L));
        Assert.NotEqual(Hash("apple"), Hash("pear"));
        Assert.NotEqual(Hash((1, 2)), Hash((2, 1)));
        Assert.NotEqual(Hash(1), Hash("1"));
        Assert.False(DurableTaskAccessArgumentHash.TryCompute(default(JournaledStateAccessNoArgument), out _));

        static int Hash<T>(T value)
        {
            Assert.True(DurableTaskAccessArgumentHash.TryCompute(value, out var hash));
            return hash;
        }
    }

    [Theory]
    [MemberData(nameof(DurableTaskJournalHarness.Formats), MemberType = typeof(DurableTaskJournalHarness))]
    public Task ReadCommitsWithTheWriteWhichMakesItsConsequenceDurable(string journalFormatKey) => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(static grain => Workflow(grain), journalFormatKey);
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(Timeout, TestCancellation);

            // Other grain work changes the counter after the body's read decided which child to start.
            activation.Grain.Counter.Value = 5;
            activation.Grain.RequestUnrelatedWrite();
            await activation.Grain.UnrelatedWrites;
        }

        // The child's start committed together with the read which chose it, so the replay chooses the same child.
        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        replay.Grain.OpenGate();
        Assert.Equal("zero", (await replay.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<string>());
        Assert.False(replay.Storage.TryGetTask(WorkflowId.Child("other"), out _));

        static async DurableTask<string> Workflow(ReplayGrain grain)
        {
            var choice = grain.Counter.Value == 0 ? "zero" : "other";
            await DurableTask.Run(static _ => { }).WithId(choice);
            await grain.WaitAtGateAsync();
            return choice;
        }
    });

    [Theory]
    [MemberData(nameof(DurableTaskJournalHarness.Formats), MemberType = typeof(DurableTaskJournalHarness))]
    public Task ReplayAfterAPartlyReplayedAttemptReturnsAllOutcomes(string journalFormatKey) => ActivationScheduler.RunAsync(async () =>
    {
        var controls = new Controls();
        await using var harness = new DurableTaskReplayHarness(grain => Workflow(grain, controls), journalFormatKey);

        // The first attempt commits its first segment's records, and its activation ends.
        await using (var first = await harness.ActivateAsync(TestCancellation))
        {
            await first.ScheduleAsync(WorkflowId, TestCancellation);
            await controls.Reached("after-one").WaitAsync(Timeout, TestCancellation);
            first.Grain.RequestUnrelatedWrite();
            await first.Grain.UnrelatedWrites;
        }

        // The second attempt replays the first segment and commits the second's records, and its activation ends.
        controls = new Controls();
        await using (var second = await harness.ActivateAsync(TestCancellation))
        {
            await controls.Pass("after-one");
            await controls.Reached("after-two").WaitAsync(Timeout, TestCancellation);
            Assert.Equal(2, second.Grain.Counter.Value);
            second.Grain.RequestUnrelatedWrite();
            await second.Grain.UnrelatedWrites;
        }

        // The third attempt replays both segments' records and applies neither increment again.
        controls = new Controls();
        await using var third = await harness.ActivateAsync(TestCancellation);
        await controls.Pass("after-one");
        await controls.Pass("after-two");
        Assert.Equal(1, (await third.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<int>());
        Assert.Equal(2, third.Grain.Counter.Value);

        static async DurableTask<int> Workflow(ReplayGrain grain, Controls controls)
        {
            var first = grain.Counter.Value++;
            await DurableTask.Run(static _ => { }).WithId("one");
            await controls.Wait("after-one");
            var second = grain.Counter.Value++;
            await DurableTask.Run(static _ => { }).WithId("two");
            await controls.Wait("after-two");
            return first * 10 + second;
        }
    });

    [Theory]
    [MemberData(nameof(DurableTaskJournalHarness.Formats), MemberType = typeof(DurableTaskJournalHarness))]
    public Task InterleavedBodiesReplayTheirOwnRecordsInAnotherOrder(string journalFormatKey) => ActivationScheduler.RunAsync(async () =>
    {
        var bodyA = TaskId.CreateRoot("a");
        var bodyB = TaskId.CreateRoot("b");
        var probes = new InterleavingProbes();
        await using var harness = new DurableTaskReplayHarness(grain => Workflow(grain, probes), journalFormatKey);
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            // Bodies interleave where a durable wait parks one: A reads the counter and parks, B writes it, then A reads
            // it again.
            var tasks = (IDurableTaskManager)activation.Runtime;
            await activation.ScheduleAsync(bodyA, TestCancellation);
            await activation.ScheduleAsync(bodyB, TestCancellation);
            await probes.FirstRead.Task.WaitAsync(Timeout, TestCancellation);
            await tasks.SignalAsync(bodyB, Next, 1, TestCancellation);
            await probes.Written.Task.WaitAsync(Timeout, TestCancellation);
            await tasks.SignalAsync(bodyA, Next, 1, TestCancellation);
            Assert.Equal("0,10", await probes.Observed.Task.WaitAsync(Timeout, TestCancellation));
            activation.Grain.RequestUnrelatedWrite();
            await activation.Grain.UnrelatedWrites;
        }

        // The replay starts both bodies from the committed state, which holds B's write, and A reads both values without B
        // running in between: each body returns its own records, so A observes the values which it observed first, and
        // B's write doesn't apply again.
        probes = new InterleavingProbes();
        await using var replay = await harness.ActivateAsync(TestCancellation);
        Assert.Equal("0,10", await probes.Observed.Task.WaitAsync(Timeout, TestCancellation));
        await probes.Written.Task.WaitAsync(Timeout, TestCancellation);
        Assert.Equal(10, replay.Grain.Counter.Value);

        static async DurableTask Workflow(ReplayGrain grain, InterleavingProbes probes)
        {
            if (DurableTask.Current.Id.ToString() == "a")
            {
                var first = grain.Counter.Value;
                probes.FirstRead.TrySetResult();
                _ = await Next.WaitAsync();
                var second = grain.Counter.Value;
                probes.Observed.TrySetResult($"{first},{second}");
            }
            else
            {
                _ = await Next.WaitAsync();
                grain.Counter.Value = 10;
                probes.Written.TrySetResult();
            }

            // The bodies stay pending, so the next activation replays them.
            _ = await End.WaitAsync();
        }
    });

    /// <summary>Lets a test observe the points which interleaved bodies reach.</summary>
    private sealed class InterleavingProbes
    {
        public TaskCompletionSource FirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<string> Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Lets a test release each named point of bodies and observe when a body reaches it.</summary>
    private sealed class Controls
    {
        private readonly Dictionary<string, (TaskCompletionSource Reached, TaskCompletionSource Release)> _points = [];

        public string? Observed { get; set; }

        public Task Reached(string name) => Get(name).Reached.Task;

        /// <summary>Waits until a body reaches <paramref name="name"/>, then lets it continue.</summary>
        public async Task Pass(string name)
        {
            await Reached(name).WaitAsync(Timeout, TestCancellation);
            Get(name).Release.TrySetResult();
        }

        public Task Wait(string name)
        {
            var point = Get(name);
            point.Reached.TrySetResult();
            return point.Release.Task.WaitAsync(DurableTask.Current.CancellationToken);
        }

        private (TaskCompletionSource Reached, TaskCompletionSource Release) Get(string name)
        {
            lock (_points)
            {
                if (!_points.TryGetValue(name, out var point))
                {
                    _points[name] = point = (new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously));
                }

                return point;
            }
        }
    }

    [Theory]
    [MemberData(nameof(DurableTaskJournalHarness.Formats), MemberType = typeof(DurableTaskJournalHarness))]
    public Task CleanupAfterTaskCancellationIsRecordedAndAppliesOnceAcrossACrash(string journalFormatKey) => ActivationScheduler.RunAsync(async () =>
    {
        var gates = new CleanupGates();
        await using var harness = new DurableTaskReplayHarness(grain => Workflow(grain, gates), journalFormatKey);
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(Timeout, TestCancellation);

            // Task cancellation is recorded, and the body's cleanup writes the counter and then awaits a durable child.
            await ((IDurableTaskManager)activation.Runtime).CancelAsync(WorkflowId, "buyer", TestCancellation);
            await gates.ChildWaiting.Task.WaitAsync(Timeout, TestCancellation);
            await activation.Grain.UnrelatedWrites.WaitAsync(Timeout, TestCancellation);
            Assert.Equal(1, activation.Grain.Counter.Value);

            // The cleanup's write has committed, and the activation crashes while the cleanup awaits the child.
        }

        // The replay observes the recorded cancellation, runs the cleanup again from its records, and the cleanup's write
        // returns its record instead of applying a second time.
        gates = new CleanupGates();
        await using var replay = await harness.ActivateAsync(TestCancellation);
        await gates.ChildWaiting.Task.WaitAsync(Timeout, TestCancellation);
        Assert.Equal(1, replay.Grain.Counter.Value);
        gates.ReleaseChild.SetResult();
        Assert.Equal(DurableTaskStatus.Canceled, (await replay.GetResponseAsync(WorkflowId, TestCancellation)).Status);
        Assert.Equal(1, replay.Grain.Counter.Value);

        static async DurableTask Workflow(ReplayGrain grain, CleanupGates gates)
        {
            try
            {
                await grain.WaitAtGateAsync();
            }
            catch (OperationCanceledException) when (DurableTask.Current.IsTaskCancellationRequested)
            {
                grain.Counter.Value++;
                grain.RequestUnrelatedWrite();

                // A canceled task's children end canceled unless shielded; the child waits until the test releases it.
                await DurableTask.Run(async _ =>
                {
                    gates.ChildWaiting.TrySetResult();
                    await gates.ReleaseChild.Task.WaitAsync(DurableTask.Current.AttemptCancellationToken);
                }).WithId("compensate").Shielded();
                throw;
            }
        }
    });

    private sealed class CleanupGates
    {
        public TaskCompletionSource ChildWaiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseChild { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [Fact]
    public void TransportSendsThroughTheOutboxAsTheJournaledStateHostsWork()
    {
        var host = new CountingHost();
        var outbox = new HostCheckingOutbox(host);
        var transport = new DurableTaskMessageTransport(new Lazy<IOutbox>(outbox), MessagingTestHelpers.MessageTypes, host);

        transport.Send(MessageId.Parse("reference", null));

        // The outbox's routing finds the host's work, so no body's context records the runtime's send.
        Assert.Equal([1], outbox.HostDepths);
        Assert.Equal(0, host.Depth);
    }

    private sealed class CountingHost : IJournaledStateContextHost
    {
        public int Depth { get; private set; }

        public JournaledStateContextProvider? ContextProvider { get; set; }

        public void EnterHostAccess() => Depth++;

        public void ExitHostAccess() => Depth--;
    }

    private sealed class HostCheckingOutbox(CountingHost host) : IOutbox
    {
        public List<int> HostDepths { get; } = [];

        public void Send(OutboxMessage message) => HostDepths.Add(host.Depth);

        public void Send(MessageId messageId) => HostDepths.Add(host.Depth);

        public bool Withdraw(MessageId messageId) => false;

        public IOutbox Register(IOutboxMessageSource source) => this;
    }

    /// <summary>Reads the kind, operation, and state of each record of an access log.</summary>
    /// <remarks>
    /// A record is <c>[kind | flags][operation][state index]</c>, where an index which equals the count of states named so
    /// far is followed by the state's <c>[id:4]</c>; then <c>[hash:4]</c> when it carries an argument hash, and
    /// <c>[length][outcome]</c> when it has an outcome or <c>[count]</c> when it refers to an earlier read, as varints.
    /// </remarks>
    internal static List<(JournaledStateAccessKind Kind, JournaledStateOperation Operation, int Target)> ParseHeaders(ReadOnlySpan<byte> log)
    {
        const byte KindMask = 0x03;
        const byte HasValue = 0x04;
        const byte HasArgumentHash = 0x10;
        const byte IsReference = 0x20;
        var states = new List<int>();
        var records = new List<(JournaledStateAccessKind, JournaledStateOperation, int)>();
        var position = 0;
        while (position < log.Length)
        {
            var flags = log[position];
            var operation = (JournaledStateOperation)log[position + 1];
            position += 2;
            var index = ReadVarUInt32(log, ref position);
            if (index == states.Count)
            {
                states.Add(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(log[position..]));
                position += 4;
            }

            records.Add(((JournaledStateAccessKind)(flags & KindMask), operation, states[index]));
            if ((flags & HasArgumentHash) != 0)
            {
                position += 4;
            }

            if ((flags & HasValue) != 0)
            {
                var length = ReadVarUInt32(log, ref position);
                position += length;
            }
            else if ((flags & IsReference) != 0)
            {
                _ = ReadVarUInt32(log, ref position);
            }
        }

        return records;

        static int ReadVarUInt32(ReadOnlySpan<byte> input, ref int position)
        {
            var value = 0;
            for (var shift = 0; ; shift += 7)
            {
                var next = input[position++];
                value |= (next & 0x7F) << shift;
                if (next < 0x80)
                {
                    return value;
                }
            }
        }
    }
}
