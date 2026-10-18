using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
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
    public Task OnceCommitsItsRecordWithItsStateChangesAndReplayReturnsTheRecordedValue() => ActivationScheduler.RunAsync(async () =>
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
    public Task OnceRunsAgainWhenTheActivationEndsBeforeAWriteCommitsIt() => ActivationScheduler.RunAsync(async () =>
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
    public Task OnceRecordsAFailureWithItsStateChangesAndReplayRethrowsIt() => ActivationScheduler.RunAsync(async () =>
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
                DurableTask.Once("charge", Charge);
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
    public Task ReplayWhichSchedulesAChildUnderAnEffectsNameFails(string journalFormatKey) => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(IncrementOnce, journalFormatKey);
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);
            activation.Grain.RequestUnrelatedWrite();
            await activation.Grain.UnrelatedWrites;
        }

        // A later version schedules a step under the name which the effect recorded, which is the effect's identifier.
        harness.Workflow = static grain => Changed(grain);
        await using var replay = await harness.ActivateAsync(TestCancellation);

        var response = await replay.GetResponseAsync(WorkflowId, TestCancellation);
        var exception = Assert.IsType<InvalidOperationException>(response.Exception);
        Assert.Contains("'workflow/increment'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("It records a DurableTask.Once effect, and this attempt requested a local child task.", exception.Message, StringComparison.Ordinal);

        static async DurableTask<int> Changed(ReplayGrain grain) =>
            await DurableTask.Run(_ => grain.Counter.Value + 1).WithId("increment");
    });

    [Theory]
    [MemberData(nameof(DurableTaskJournalHarness.Formats), MemberType = typeof(DurableTaskJournalHarness))]
    public Task ReplayWhichRunsAnEffectUnderAStepsNameFails(string journalFormatKey) => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(static grain => Original(grain), journalFormatKey);
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);
        }

        // A later version replaces the recorded step with an effect of the same name.
        harness.Workflow = IncrementOnce;
        await using var replay = await harness.ActivateAsync(TestCancellation);

        var response = await replay.GetResponseAsync(WorkflowId, TestCancellation);
        var exception = Assert.IsType<InvalidOperationException>(response.Exception);
        Assert.Contains("It records a local child task, and this attempt requested a DurableTask.Once effect.", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, harness.EffectRuns);

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
    public Task ReplayWhichRunsAnEffectUnderTheSameOccurrenceOfANameFails(string original) => ActivationScheduler.RunAsync(async () =>
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
        harness.Workflow = static grain => Changed(grain);
        await using var replay = await harness.ActivateAsync(TestCancellation);

        var response = await replay.GetResponseAsync(WorkflowId, TestCancellation);
        var exception = Assert.IsType<InvalidOperationException>(response.Exception);
        Assert.Contains("'workflow/increment$2'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("this attempt requested a DurableTask.Once effect.", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, harness.EffectRuns);
        Assert.Equal(2, replay.Grain.Counter.Value);

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
            return DurableTask.Once("increment", () =>
            {
                grain.Harness.CountEffectRun();
                return ++grain.Counter.Value;
            });
        }
    });

    [Fact]
    public Task ReplayWhichRunsACombinatorInputBeneathAnEffectsNameFails() => ActivationScheduler.RunAsync(async () =>
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
        harness.Workflow = static grain => Changed(grain);
        await using var replay = await harness.ActivateAsync(TestCancellation);

        var response = await replay.GetResponseAsync(WorkflowId, TestCancellation);
        var exception = Assert.IsType<InvalidOperationException>(response.Exception);
        Assert.Contains("'workflow/increment/0' runs beneath 'workflow/increment'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("It records a DurableTask.Once effect, and this attempt requested a local child task.", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, replay.Grain.Counter.Value);

        static async DurableTask<int> Changed(ReplayGrain grain)
        {
            await DurableTask.WhenAll([DurableTask.Run(_ => ++grain.Counter.Value)]).WithId("increment");
            return grain.Counter.Value;
        }
    });

    [Fact]
    public Task OnceRecordsAResultWhichDoesNotSerializeAsThatFailure() => ActivationScheduler.RunAsync(async () =>
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
                DurableTask.Once("value", () =>
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
    public Task UnnamedOnceCommitsItsRecordUnderAGeneratedIdentifierAndReplayReturnsTheRecordedValue() => ActivationScheduler.RunAsync(async () =>
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
            Assert.Equal("$once", GetTask(activation, effectId).RequestFingerprint);
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
            var second = DurableTask.Once(() =>
            {
                grain.Harness.CountEffectRun();
                return ++grain.Counter.Value;
            });
            await grain.WaitAtGateAsync();
            return first + second;
        }
    });

    [Fact]
    public Task ReplayWhichRunsAnUnnamedEffectWhereAnUnnamedChildWasRecordedFails() => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(static grain => Original(grain));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);
        }

        // A later version replaces the unnamed step with an unnamed effect, which takes the step's generated identifier.
        harness.Workflow = static grain => Changed(grain);
        await using var replay = await harness.ActivateAsync(TestCancellation);

        var response = await replay.GetResponseAsync(WorkflowId, TestCancellation);
        var exception = Assert.IsType<InvalidOperationException>(response.Exception);
        Assert.Contains("'workflow/$child-1'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("It records a local child task, and this attempt requested a DurableTask.Once effect.", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, harness.EffectRuns);
        Assert.Equal(1, replay.Grain.Counter.Value);

        static async DurableTask<int> Original(ReplayGrain grain)
        {
            var value = await DurableTask.Run(_ => ++grain.Counter.Value);
            await grain.WaitAtGateAsync();
            return value;
        }

        static async DurableTask<int> Changed(ReplayGrain grain)
        {
            var value = DurableTask.Once(() =>
            {
                grain.Harness.CountEffectRun();
                return ++grain.Counter.Value;
            });
            await grain.WaitAtGateAsync();
            return value;
        }
    });

    [Fact]
    public Task ReplayWhichSchedulesAnUnnamedChildWhereAnUnnamedEffectWasRecordedFails() => ActivationScheduler.RunAsync(async () =>
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
        harness.Workflow = grain => Changed(grain, () => childRuns++);
        await using var replay = await harness.ActivateAsync(TestCancellation);

        var response = await replay.GetResponseAsync(WorkflowId, TestCancellation);
        var exception = Assert.IsType<InvalidOperationException>(response.Exception);
        Assert.Contains("'workflow/$child-1'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("It records a DurableTask.Once effect", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, childRuns);
        Assert.Equal(1, harness.EffectRuns);
        Assert.Equal(1, replay.Grain.Counter.Value);

        static async DurableTask<int> Original(ReplayGrain grain)
        {
            var value = DurableTask.Once(() =>
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
    public Task OnceAfterConfigureAwaitFalseFailsWithoutRunning() => ActivationScheduler.RunAsync(async () =>
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
            return DurableTask.Once("increment", () =>
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
        var a = DurableTask.Once("a", static () => 1);
        Observe("a");
        if (insertOperation)
        {
            DurableTask.Once("inserted", static () => 0);
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
        var value = DurableTask.Once("increment", () =>
        {
            grain.Harness.CountEffectRun();
            return ++grain.Counter.Value;
        });
        await grain.WaitAtGateAsync();
        return value;
    }

    /// <summary>A result type which has no serializer.</summary>
    private sealed class UnserializableResult;

    private static IDurableTaskState GetTask(ReplayActivation activation, TaskId taskId)
    {
        Assert.True(activation.Storage.TryGetTask(taskId, out var state));
        return state;
    }
}
