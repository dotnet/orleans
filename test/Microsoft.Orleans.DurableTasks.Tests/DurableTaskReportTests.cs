using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.Journaling;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>
/// Verifies that a durable task's progress reports append to a journaled log exactly once per position, commit with the
/// task's next journal write, and return their recorded sequence numbers on replay.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class DurableTaskReportTests
{
    private static readonly TaskId WorkflowId = TaskId.CreateRoot("workflow");

    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    [Fact]
    public Task ReportCommitsItsEntryWithItsRecordAndReplayReturnsTheRecordedSequence() => ActivationScheduler.RunAsync(async () =>
    {
        var reportId = WorkflowId.Child("$child-1");
        await using var harness = new DurableTaskReplayHarness(static grain => ReportOnce(grain, 10));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);
            Assert.Equal(0, activation.Grain.Log.CommittedSequence);

            // Unrelated grain work writes the journal while the workflow waits; the write gathers the entry together
            // with the report's record.
            activation.Grain.RequestUnrelatedWrite();
            await activation.Grain.UnrelatedWrites;

            Assert.Equal(1, activation.Grain.Log.CommittedSequence);
            var entryWrites = activation.Writes.Where(static write => write.Entries == 1).ToArray();
            Assert.NotEmpty(entryWrites);
            Assert.All(entryWrites, write => Assert.Contains(reportId, write.Completed));
            var record = GetTask(activation, reportId);
            Assert.Equal("$once", record.RequestFingerprint);
            Assert.Equal(0L, record.Result!.GetResult<long>());
        }

        // The replay returns the recorded sequence without appending again.
        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);
        Assert.Equal([10], replay.Grain.Log.ToArray());
        replay.Grain.OpenGate();

        Assert.Equal(0L, (await replay.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<long>());
        Assert.Equal([10], replay.Grain.Log.ToArray());
        Assert.Equal(1, replay.Grain.Log.NextSequence);
    });

    [Fact]
    public Task ReportAppendsAgainOnceWhenTheActivationEndsBeforeAWriteCommitsIt() => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(static grain => ReportOnce(grain, 10));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);
            Assert.Equal([10], activation.Grain.Log.ToArray());
            Assert.Equal(0, activation.Grain.Log.CommittedSequence);
        }

        // No write committed the entry, so it was lost with its record, and the replay appends it again, once.
        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);
        Assert.Equal([10], replay.Grain.Log.ToArray());
        replay.Grain.OpenGate();

        Assert.Equal(0L, (await replay.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<long>());
        Assert.Equal([10], replay.Grain.Log.ToArray());
        Assert.Equal(1, replay.Grain.Log.CommittedSequence);
    });

    [Fact]
    public Task ReportsKeepTheirPositionsWhenAReplayFindsSomeOfThemCommitted() => ActivationScheduler.RunAsync(async () =>
    {
        var sequences = new List<long[]>();
        await using var harness = new DurableTaskReplayHarness(grain => Workflow(grain, sequences));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);

            // The step's write committed the first report; the later reports are staged.
            Assert.Equal([1, 2, 3], activation.Grain.Log.ToArray());
            Assert.Equal(1, activation.Grain.Log.CommittedSequence);
        }

        // The replay returns the first report's recorded sequence and appends the reports which no write committed, in
        // their positions.
        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);
        replay.Grain.OpenGate();
        await replay.GetResponseAsync(WorkflowId, TestCancellation);

        Assert.Equal([1, 2, 3], replay.Grain.Log.ToArray());
        Assert.Equal(2, sequences.Count);
        Assert.All(sequences, static run => Assert.Equal([0L, 1L, 2L], run));
        Assert.Equal(
            ["workflow/$child-1", "workflow/$child-3", "workflow/$child-4"],
            replay.Storage.Tasks
                .Where(static task => task.State.RequestFingerprint == "$once")
                .Select(static task => task.Id.ToString())
                .Order(StringComparer.Ordinal));

        static async DurableTask Workflow(ReplayGrain grain, List<long[]> sequences)
        {
            var first = grain.Log.Report(1);
            await DurableTask.Run(static _ => { });
            var second = grain.Log.Report(2);
            var third = grain.Log.Report(3);
            sequences.Add([first, second, third]);
            await grain.WaitAtGateAsync();
        }
    });

    [Fact]
    public Task ReportOutsideADurableTaskAppendsDirectly() => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(static grain => ReportOnce(grain, 10));
        await using var activation = await harness.ActivateAsync(TestCancellation);

        Assert.Equal(0L, activation.Grain.Log.Report(5));
        Assert.Equal(1L, activation.Grain.Log.Report(6));

        Assert.Equal([5, 6], activation.Grain.Log.ToArray());
        Assert.Empty(activation.Storage.Tasks);
    });

    [Fact]
    public Task ReportAfterConfigureAwaitFalseFailsWithoutAppending() => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(static grain => Workflow(grain));
        await using var activation = await harness.ActivateAsync(TestCancellation);

        await activation.ScheduleAsync(WorkflowId, TestCancellation);

        var response = await activation.GetResponseAsync(WorkflowId, TestCancellation);
        var exception = Assert.IsType<InvalidOperationException>(response.Exception);
        Assert.Contains("IDurableLog<T>.Report", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ConfigureAwait(false)", exception.Message, StringComparison.Ordinal);
        Assert.Empty(activation.Grain.Log);

        static async DurableTask<long> Workflow(ReplayGrain grain)
        {
            await Task.Delay(1).ConfigureAwait(false);
            return grain.Log.Report(1);
        }
    });

    [Fact]
    public Task ReportRecordsAFailedAppendAndReplayRethrowsIt() => ActivationScheduler.RunAsync(async () =>
    {
        var reportId = WorkflowId.Child("$child-1");
        await using var harness = new DurableTaskReplayHarness(static grain => Workflow(grain));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            activation.Grain.Log.Seal();
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);
            activation.Grain.RequestUnrelatedWrite();
            await activation.Grain.UnrelatedWrites;

            Assert.IsType<InvalidOperationException>(GetTask(activation, reportId).Result!.Exception);
        }

        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);
        replay.Grain.OpenGate();

        Assert.Equal("failed", (await replay.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<string>());
        Assert.Empty(replay.Grain.Log);

        static async DurableTask<string> Workflow(ReplayGrain grain)
        {
            string outcome;
            try
            {
                grain.Log.Report(1);
                outcome = "appended";
            }
            catch (InvalidOperationException)
            {
                outcome = "failed";
            }

            await grain.WaitAtGateAsync();
            return outcome;
        }
    });

    [Fact]
    public Task ReplayReportsIsReplayingUntilItReachesEveryRecordedReport() => ActivationScheduler.RunAsync(async () =>
    {
        var observed = new List<string>();
        await using var harness = new DurableTaskReplayHarness(grain => Workflow(grain, observed));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);
        }

        Assert.Equal(["start:False", "reported:False", "stepped:False", "b:False"], observed);
        observed.Clear();

        // The step committed the first report. The replay reaches the report and then the step, which the first attempt
        // recorded, and appends the second report as new work.
        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);
        replay.Grain.OpenGate();
        await replay.GetResponseAsync(WorkflowId, TestCancellation);

        Assert.Equal(["start:True", "reported:True", "stepped:False", "b:False", "gate:False"], observed);
        Assert.Equal([1, 2], replay.Grain.Log.ToArray());

        static async DurableTask Workflow(ReplayGrain grain, List<string> observed)
        {
            Observe("start");
            grain.Log.Report(1);
            Observe("reported");
            await DurableTask.Run(static _ => { });
            Observe("stepped");
            grain.Log.Report(2);
            Observe("b");
            await grain.WaitAtGateAsync();
            Observe("gate");

            void Observe(string point) => observed.Add($"{point}:{DurableTask.Current.IsReplaying}");
        }
    });

    /// <summary>Reports <paramref name="item"/>, then waits at the grain's gate and returns the report's sequence.</summary>
    private static DurableTask ReportOnce(ReplayGrain grain, int item) => ReportOnceAsync(grain, item);

    private static async DurableTask<long> ReportOnceAsync(ReplayGrain grain, int item)
    {
        var sequence = grain.Log.Report(item);
        await grain.WaitAtGateAsync();
        return sequence;
    }

    private static IDurableTaskState GetTask(ReplayActivation activation, TaskId taskId)
    {
        Assert.True(activation.Storage.TryGetTask(taskId, out var state));
        return state;
    }
}
