using Orleans.DurableTasks;
using Orleans.DurableTasks.Diagnostics;
using Orleans.DurableTasks.Protocol;
using Orleans.Messaging;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>
/// Verifies which journal writes commit a workflow's state changes, and what a replay observes after an activation ends.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class DurableTaskReplaySafetyTests
{
    private static readonly TaskId WorkflowId = TaskId.CreateRoot("workflow");

    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    [Fact]
    public Task TailAfterADurableAwaitCommitsItsStateChangesWithTheResult() => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(Workflow);
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);

            Assert.Equal(1, (await activation.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<int>());
            await activation.Grain.UnrelatedWrites;
            AssertTailCommitsWithResult(activation);
        }

        // The next activation recovers the result, so nothing replays the tail.
        await using var recovered = await harness.ActivateAsync(TestCancellation);
        Assert.Equal(1, recovered.Grain.Counter.Value);
        Assert.Equal(1, (await recovered.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<int>());
        Assert.Equal(1, harness.EffectRuns);

        static async DurableTask<int> Workflow(ReplayGrain grain)
        {
            await DurableTask.Run(static _ => { }).WithId("first");

            // The tail's state change and a write which other grain work requests.
            grain.Harness.CountEffectRun();
            grain.Counter.Value++;
            grain.RequestUnrelatedWrite();
            return grain.Counter.Value;
        }
    });

    [Fact]
    public Task TailOfAnAsyncDelegateCommitsItsStateChangesWithTheResult() => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(static grain => DurableTask.Run(async _ =>
        {
            await Task.Yield();
            grain.Counter.Value++;
            grain.RequestUnrelatedWrite();
            return grain.Counter.Value;
        }));
        await using var activation = await harness.ActivateAsync(TestCancellation);

        await activation.ScheduleAsync(WorkflowId, TestCancellation);

        Assert.Equal(1, (await activation.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<int>());
        await activation.Grain.UnrelatedWrites;
        AssertTailCommitsWithResult(activation);
    });

    /// <summary>
    /// Asserts that every write which commits the tail's change also commits the workflow's result, so no replay applies
    /// the change again.
    /// </summary>
    private static void AssertTailCommitsWithResult(ReplayActivation activation)
    {
        var tailWrites = activation.Writes.Where(static write => write.Counter == 1).ToArray();
        Assert.NotEmpty(tailWrites);
        Assert.All(tailWrites, static write => Assert.Contains(WorkflowId, write.Completed));
    }

    [Fact]
    public Task RunInlineCommitsItsRecordWithItsStateChangesAndReplayReturnsTheRecordedValue() => ActivationScheduler.RunAsync(async () =>
    {
        var effectId = WorkflowId.Child("increment");
        await using var harness = new DurableTaskReplayHarness(IncrementOnce);
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);
            var writesBefore = activation.Writes.Count;

            // Unrelated grain work writes the journal while the workflow waits after the effect; the write gathers the
            // effect's state change together with its record.
            activation.Grain.RequestUnrelatedWrite();
            await activation.Grain.UnrelatedWrites;

            Assert.Equal(1, harness.EffectRuns);
            Assert.True(activation.Writes.Count > writesBefore);
            Assert.All(
                activation.Writes.Where(static write => write.Counter == 1),
                write => Assert.Contains(effectId, write.Completed));
        }

        // The activation ends while the workflow waits. The replay returns the recorded value without running the
        // effect, and the counter holds the effect's single change.
        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);
        Assert.Equal(1, replay.Grain.Counter.Value);
        Assert.Equal(1, harness.EffectRuns);
        replay.Grain.OpenGate();

        Assert.Equal(1, (await replay.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<int>());
        Assert.Equal(1, replay.Grain.Counter.Value);
    });

    [Fact]
    public Task RunInlineRunsAgainWhenTheActivationEndsBeforeAWriteCommitsIt() => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(IncrementOnce);
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);
            Assert.Equal(1, activation.Grain.Counter.Value);
        }

        // No write committed the effect, so its state change was lost with its record, and the replay runs it again.
        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);
        Assert.Equal(2, harness.EffectRuns);
        Assert.Equal(1, replay.Grain.Counter.Value);
        replay.Grain.OpenGate();

        Assert.Equal(1, (await replay.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<int>());
    });

    [Fact]
    public Task RunInlineRecordsAFailureWithItsStateChangesAndReplayRethrowsIt() => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(Workflow);
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);
            activation.Grain.RequestUnrelatedWrite();
            await activation.Grain.UnrelatedWrites;
        }

        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);
        replay.Grain.OpenGate();

        Assert.Equal("declined at 1", (await replay.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<string>());
        Assert.Equal(1, harness.EffectRuns);
        Assert.Equal(1, replay.Grain.Counter.Value);

        static async DurableTask<string> Workflow(ReplayGrain grain)
        {
            string outcome;
            try
            {
                DurableTask.RunInline("charge", Charge);
                outcome = "charged";
            }
            catch (InvalidOperationException exception)
            {
                outcome = exception.Message;
            }

            await grain.WaitAtGateAsync();
            return outcome;

            int Charge()
            {
                // Journaled state has no rollback, so this change commits with the recorded failure.
                grain.Harness.CountEffectRun();
                grain.Counter.Value++;
                throw new InvalidOperationException($"declined at {grain.Counter.Value}");
            }
        }
    });

    [Theory]
    [MemberData(nameof(DurableTaskJournalHarness.Formats), MemberType = typeof(DurableTaskJournalHarness))]
    public Task ReplayWhichSchedulesAChildUnderAnEffectsNameStallsUntilCodeWhichMatchesTheRecordRuns(string journalFormatKey) => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(IncrementOnce, journalFormatKey);
        harness.Options.StalledExecutionRetryInterval = TimeSpan.FromSeconds(7);
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);
            activation.Grain.RequestUnrelatedWrite();
            await activation.Grain.UnrelatedWrites;
        }

        // A later version schedules a step under the name which the effect recorded, which is the effect's identifier.
        var stalled = await AssertReplayStallsAsync(harness, static grain => Changed(grain));
        Assert.Contains("'workflow/increment'", stalled.Exception.Message, StringComparison.Ordinal);
        Assert.Contains("It records a DurableTask.RunInline effect, and this attempt requested a local child task.", stalled.Exception.Message, StringComparison.Ordinal);

        // Code which matches the record replays the execution, which completes without running the effect again.
        Assert.Equal(1, await CompleteAtGateAsync(harness));
        Assert.Equal(1, harness.EffectRuns);

        static async DurableTask<int> Changed(ReplayGrain grain) =>
            await DurableTask.Run(_ => grain.Counter.Value + 1).WithId("increment");
    });

    [Theory]
    [MemberData(nameof(DurableTaskJournalHarness.Formats), MemberType = typeof(DurableTaskJournalHarness))]
    public Task ReplayWhichRunsAnEffectUnderAStepsNameStallsUntilCodeWhichMatchesTheRecordRuns(string journalFormatKey) => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(static grain => Original(grain), journalFormatKey);
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);
        }

        // A later version replaces the recorded step with an effect of the same name.
        var stalled = await AssertReplayStallsAsync(harness, IncrementOnce);
        Assert.Contains("It records a local child task, and this attempt requested a DurableTask.RunInline effect.", stalled.Exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, harness.EffectRuns);

        Assert.Equal(1, await CompleteAtGateAsync(harness));

        static async DurableTask<int> Original(ReplayGrain grain)
        {
            var value = await DurableTask.Run(_ => ++grain.Counter.Value).WithId("increment");
            await grain.WaitAtGateAsync();
            return value;
        }
    });

    [Theory]
    [InlineData("step")]
    [InlineData("combinator")]
    public Task ReplayWhichRunsAnEffectUnderTheSameOccurrenceOfANameStalls(string original) => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(grain => Original(grain, original == "combinator"));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);
        }

        // A later version keeps the first step and turns the name's second occurrence into an effect. Names count their
        // occurrences across kinds, so the effect's identifier is the recorded increment$2, which holds the step or the
        // combinator's input, rather than the first step's.
        var stalled = await AssertReplayStallsAsync(harness, static grain => Changed(grain));
        Assert.Contains("'workflow/increment$2'", stalled.Exception.Message, StringComparison.Ordinal);
        Assert.Contains("this attempt requested a DurableTask.RunInline effect.", stalled.Exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, harness.EffectRuns);

        Assert.Equal(2, await CompleteAtGateAsync(harness));

        static async DurableTask<int> Original(ReplayGrain grain, bool combinator)
        {
            await DurableTask.Run(_ => ++grain.Counter.Value).WithId("increment");
            if (combinator)
            {
                await DurableTask.WhenAll([DurableTask.Run(_ => ++grain.Counter.Value)]).WithId("increment");
            }
            else
            {
                await DurableTask.Run(_ => ++grain.Counter.Value).WithId("increment");
            }

            await grain.WaitAtGateAsync();
            return grain.Counter.Value;
        }

        static async DurableTask<int> Changed(ReplayGrain grain)
        {
            await DurableTask.Run(_ => ++grain.Counter.Value).WithId("increment");
            return DurableTask.RunInline("increment", () =>
            {
                grain.Harness.CountEffectRun();
                return ++grain.Counter.Value;
            });
        }
    });

    [Fact]
    public Task ReplayWhichRunsACombinatorInputBeneathAnEffectsNameStalls() => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(IncrementOnce);
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);
            activation.Grain.RequestUnrelatedWrite();
            await activation.Grain.UnrelatedWrites;
        }

        // A later version turns the effect into a combinator of the same name, whose input would run beneath the effect.
        var stalled = await AssertReplayStallsAsync(harness, static grain => Changed(grain));
        Assert.Contains("'workflow/increment/0' runs beneath 'workflow/increment'", stalled.Exception.Message, StringComparison.Ordinal);
        Assert.Contains("It records a DurableTask.RunInline effect, and this attempt requested a local child task.", stalled.Exception.Message, StringComparison.Ordinal);

        Assert.Equal(1, await CompleteAtGateAsync(harness));
        Assert.Equal(1, harness.EffectRuns);

        static async DurableTask<int> Changed(ReplayGrain grain)
        {
            await DurableTask.WhenAll([DurableTask.Run(_ => ++grain.Counter.Value)]).WithId("increment");
            return grain.Counter.Value;
        }
    });

    [Theory]
    [MemberData(nameof(DurableTaskJournalHarness.Formats), MemberType = typeof(DurableTaskJournalHarness))]
    public Task ReplayWhoseRecordedOutcomeNoLongerDecodesStallsUntilCodeWhichDecodesItRuns(string journalFormatKey) => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(static grain => Workflow(grain), journalFormatKey);
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);
            activation.Grain.RequestUnrelatedWrite();
            await activation.Grain.UnrelatedWrites;
        }

        // A deployment whose code no longer decodes the recorded value replays the execution.
        DecodingProbe.FailsDecoding = true;
        try
        {
            var stalled = await AssertReplayStallsAsync(harness, static grain => Workflow(grain));
            Assert.Contains("'workflow/reading' no longer decodes", stalled.Exception.Message, StringComparison.Ordinal);
            Assert.IsType<InvalidOperationException>(stalled.Exception.InnerException, exactMatch: false);
        }
        finally
        {
            DecodingProbe.FailsDecoding = false;
        }

        // Code which decodes the value replays the execution, which completes without running the effect again.
        Assert.Equal(7, await CompleteAtGateAsync(harness));
        Assert.Equal(1, harness.EffectRuns);

        static async DurableTask<int> Workflow(ReplayGrain grain)
        {
            var reading = DurableTask.RunInline("reading", () =>
            {
                grain.Harness.CountEffectRun();
                return new DecodingProbe { Value = 7 };
            });
            await grain.WaitAtGateAsync();
            return reading.Value;
        }
    });

    [Fact]
    public Task RunInlineRecordsAResultWhichDoesNotSerializeAsThatFailure() => ActivationScheduler.RunAsync(async () =>
    {
        string? firstOutcome = null;
        await using var harness = new DurableTaskReplayHarness(Workflow);
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);
            activation.Grain.RequestUnrelatedWrite();
            await activation.Grain.UnrelatedWrites;
        }

        // The record holds the failure, so the replay throws it without running the effect again.
        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);
        replay.Grain.OpenGate();

        var outcome = (await replay.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<string>();
        Assert.StartsWith("failed:", firstOutcome, StringComparison.Ordinal);
        Assert.Equal(firstOutcome, outcome);
        Assert.Equal(1, harness.EffectRuns);
        Assert.Equal(1, replay.Grain.Counter.Value);

        async DurableTask<string> Workflow(ReplayGrain grain)
        {
            string result;
            try
            {
                DurableTask.RunInline("value", () =>
                {
                    grain.Harness.CountEffectRun();
                    grain.Counter.Value++;
                    return new UnserializableResult();
                });
                result = "recorded";
            }
            catch (Exception exception)
            {
                result = $"failed:{exception.GetType().Name}";
            }

            firstOutcome ??= result;
            await grain.WaitAtGateAsync();
            return result;
        }
    });

    [Fact]
    public Task UnnamedRunInlineCommitsItsRecordUnderAGeneratedIdentifierAndReplayReturnsTheRecordedValue() => ActivationScheduler.RunAsync(async () =>
    {
        var effectId = WorkflowId.Child("$child-2");
        await using var harness = new DurableTaskReplayHarness(static grain => Workflow(grain));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);
            activation.Grain.RequestUnrelatedWrite();
            await activation.Grain.UnrelatedWrites;

            Assert.All(
                activation.Writes.Where(static write => write.Counter == 2),
                write => Assert.Contains(effectId, write.Completed));
            Assert.Equal("$effect", GetTask(activation, effectId).RequestFingerprint);
        }

        // The replay creates the unnamed child and the unnamed effect in the same order, so each finds its record.
        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);
        Assert.Equal(1, harness.EffectRuns);
        Assert.Equal(2, replay.Grain.Counter.Value);
        replay.Grain.OpenGate();

        Assert.Equal(3, (await replay.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<int>());
        Assert.Equal(2, replay.Grain.Counter.Value);

        static async DurableTask<int> Workflow(ReplayGrain grain)
        {
            var first = await DurableTask.Run(_ => ++grain.Counter.Value);
            var second = DurableTask.RunInline(() =>
            {
                grain.Harness.CountEffectRun();
                return ++grain.Counter.Value;
            });
            await grain.WaitAtGateAsync();
            return first + second;
        }
    });

    [Fact]
    public Task ReplayWhichRunsAnUnnamedEffectWhereAnUnnamedChildWasRecordedStalls() => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(static grain => Original(grain));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);
        }

        // A later version replaces the unnamed step with an unnamed effect, which takes the step's generated identifier.
        var stalled = await AssertReplayStallsAsync(harness, static grain => Changed(grain));
        Assert.Contains("'workflow/$child-1'", stalled.Exception.Message, StringComparison.Ordinal);
        Assert.Contains("It records a local child task, and this attempt requested a DurableTask.RunInline effect.", stalled.Exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, harness.EffectRuns);

        Assert.Equal(1, await CompleteAtGateAsync(harness));

        static async DurableTask<int> Original(ReplayGrain grain)
        {
            var value = await DurableTask.Run(_ => ++grain.Counter.Value);
            await grain.WaitAtGateAsync();
            return value;
        }

        static async DurableTask<int> Changed(ReplayGrain grain)
        {
            var value = DurableTask.RunInline(() =>
            {
                grain.Harness.CountEffectRun();
                return ++grain.Counter.Value;
            });
            await grain.WaitAtGateAsync();
            return value;
        }
    });

    [Fact]
    public Task ReplayWhichSchedulesAnUnnamedChildWhereAnUnnamedEffectWasRecordedStalls() => ActivationScheduler.RunAsync(async () =>
    {
        var childRuns = 0;
        await using var harness = new DurableTaskReplayHarness(static grain => Original(grain));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);
            activation.Grain.RequestUnrelatedWrite();
            await activation.Grain.UnrelatedWrites;
        }

        // A later version replaces the unnamed effect with an unnamed step, which takes the effect's generated identifier.
        var stalled = await AssertReplayStallsAsync(harness, grain => Changed(grain, () => childRuns++));
        Assert.Contains("'workflow/$child-1'", stalled.Exception.Message, StringComparison.Ordinal);
        Assert.Contains("It records a DurableTask.RunInline effect", stalled.Exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, childRuns);

        Assert.Equal(1, await CompleteAtGateAsync(harness));
        Assert.Equal(1, harness.EffectRuns);

        static async DurableTask<int> Original(ReplayGrain grain)
        {
            var value = DurableTask.RunInline(() =>
            {
                grain.Harness.CountEffectRun();
                return ++grain.Counter.Value;
            });
            await grain.WaitAtGateAsync();
            return value;
        }

        static async DurableTask<int> Changed(ReplayGrain grain, Action run) =>
            await DurableTask.Run(_ =>
            {
                run();
                return ++grain.Counter.Value;
            });
    });

    [Fact]
    public Task RunInlineAfterConfigureAwaitFalseFailsWithoutRunning() => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(static grain => Workflow(grain));
        await using var activation = await harness.ActivateAsync(TestCancellation);

        await activation.ScheduleAsync(WorkflowId, TestCancellation);

        var response = await activation.GetResponseAsync(WorkflowId, TestCancellation);
        Assert.Contains("ConfigureAwait(false)", Assert.IsType<InvalidOperationException>(response.Exception).Message, StringComparison.Ordinal);
        Assert.Equal(0, harness.EffectRuns);

        static async DurableTask<int> Workflow(ReplayGrain grain)
        {
            await Task.Delay(1).ConfigureAwait(false);
            return DurableTask.RunInline("increment", () =>
            {
                grain.Harness.CountEffectRun();
                return ++grain.Counter.Value;
            });
        }
    });

    [Fact]
    public Task ReplayReportsIsReplayingUntilItReachesEveryRecordedOperation() => ActivationScheduler.RunAsync(async () =>
    {
        var observed = new List<string>();
        await using var harness = new DurableTaskReplayHarness(grain => Workflow(grain, observed, insertOperation: false));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);
        }

        Assert.Equal(["start:False", "a:False", "b:False"], observed);
        observed.Clear();

        // The replay reaches the effect and the step which the first attempt recorded, in that order.
        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);
        replay.Grain.OpenGate();
        Assert.Equal(3, (await replay.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<int>());

        Assert.Equal(["start:True", "a:True", "b:False", "gate:False"], observed);
    });

    [Fact]
    public Task ReplayWhichStartsANewOperationStopsReplaying() => ActivationScheduler.RunAsync(async () =>
    {
        var observed = new List<string>();
        var insertOperation = false;
        await using var harness = new DurableTaskReplayHarness(grain => Workflow(grain, observed, insertOperation));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);
        }

        // A later version adds an effect between the recorded ones, so its replay runs new work before reaching "b".
        observed.Clear();
        insertOperation = true;
        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);
        replay.Grain.OpenGate();
        Assert.Equal(3, (await replay.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<int>());

        Assert.Equal(["start:True", "a:True", "inserted:False", "b:False", "gate:False"], observed);
    });

    private static async DurableTask<int> Workflow(ReplayGrain grain, List<string> observed, bool insertOperation)
    {
        Observe("start");
        var a = DurableTask.RunInline("a", static () => 1);
        Observe("a");
        if (insertOperation)
        {
            DurableTask.RunInline("inserted", static () => 0);
            Observe("inserted");
        }

        var b = await DurableTask.Run(static _ => 2).WithId("b");
        Observe("b");
        await grain.WaitAtGateAsync();
        Observe("gate");
        return a + b;

        void Observe(string point) => observed.Add($"{point}:{DurableTask.Current.IsReplaying}");
    }

    /// <summary>Increments the grain's counter in an effect, then waits at the grain's gate.</summary>
    private static DurableTask IncrementOnce(ReplayGrain grain) => IncrementOnceAsync(grain);

    private static async DurableTask<int> IncrementOnceAsync(ReplayGrain grain)
    {
        var value = DurableTask.RunInline("increment", () =>
        {
            grain.Harness.CountEffectRun();
            return ++grain.Counter.Value;
        });
        await grain.WaitAtGateAsync();
        return value;
    }

    /// <summary>
    /// Replays the recorded workflow with <paramref name="changed"/> code, asserts that the attempt stalls as diverged and
    /// keeps the task pending, and restores the harness's workflow, so the next activation runs code which matches the
    /// record.
    /// </summary>
    private static async Task<DurableTaskEvents.ExecutionStalled> AssertReplayStallsAsync(
        DurableTaskReplayHarness harness,
        Func<ReplayGrain, DurableTask> changed)
    {
        var original = harness.Workflow;
        using var stalls = new StallRecorder(WorkflowId);
        harness.Workflow = changed;
        DurableTaskEvents.ExecutionStalled stalled;
        await using (var replay = await harness.ActivateAsync(TestCancellation))
        {
            stalled = await stalls.Stalled.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);
            Assert.Equal(DurableTaskAttemptCancellationReason.Diverged, stalled.Reason);
            Assert.IsAssignableFrom<InvalidOperationException>(stalled.Exception);
            Assert.True(replay.Storage.TryGetTask(WorkflowId, out var state));
            Assert.False(state.Result is { IsCompleted: true });

            // The grain keeps a wake-up at the configured interval, so a later activation replays the stalled execution.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (((IInboxWorkSource)replay.Runtime).GetWakeupDelay() != harness.Options.StalledExecutionRetryInterval)
            {
                Assert.True(DateTime.UtcNow < deadline, "The stalled execution keeps the configured wake-up.");
                await Task.Delay(10, TestCancellation);
            }
        }

        harness.Workflow = original;
        return stalled;
    }

    /// <summary>Activates the grain, lets the recovered workflow pass its gate, and returns the workflow's result.</summary>
    private static async Task<int> CompleteAtGateAsync(DurableTaskReplayHarness harness)
    {
        await using var activation = await harness.ActivateAsync(TestCancellation);
        await activation.Grain.AtGate.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);
        activation.Grain.OpenGate();
        return (await activation.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<int>();
    }

    /// <summary>A recorded value whose decoding fails while <see cref="FailsDecoding"/> is set, as after a deployment.</summary>
    [GenerateSerializer]
    [Alias("Microsoft.Orleans.DurableTasks.Tests.DecodingProbe")]
    internal sealed class DecodingProbe
    {
        [NonSerialized]
        private int _value;

        /// <summary>Gets or sets whether decoding fails, which one test sets.</summary>
        public static volatile bool FailsDecoding;

        [Id(0)]
        public int Value
        {
            get => _value;
            set => _value = FailsDecoding ? throw new InvalidOperationException("The deployed code can't decode this value.") : value;
        }
    }

    /// <summary>A result type which has no serializer.</summary>
    private sealed class UnserializableResult;

    private static IDurableTaskState GetTask(ReplayActivation activation, TaskId taskId)
    {
        Assert.True(activation.Storage.TryGetTask(taskId, out var state));
        return state;
    }
}
