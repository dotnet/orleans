using Orleans.DurableTasks;
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
        var effectId = WorkflowId.Child("$once:increment");
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

    [Fact]
    public Task ReplayWhichSchedulesAChildUnderAnEffectsNameFails() => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(IncrementOnce);
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);
            activation.Grain.RequestUnrelatedWrite();
            await activation.Grain.UnrelatedWrites;
        }

        // A later version schedules a step under the name which the effect recorded.
        harness.Workflow = static grain => Changed(grain);
        await using var replay = await harness.ActivateAsync(TestCancellation);

        var response = await replay.GetResponseAsync(WorkflowId, TestCancellation);
        var exception = Assert.IsType<InvalidOperationException>(response.Exception);
        Assert.Contains("It records a DurableTask.Once effect, and this attempt requested a local child task.", exception.Message, StringComparison.Ordinal);

        static async DurableTask<int> Changed(ReplayGrain grain) =>
            await DurableTask.Run(_ => grain.Counter.Value + 1).WithId("increment");
    });

    [Fact]
    public Task ReplayWhichRunsAnEffectUnderAStepsNameFails() => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(static grain => Original(grain));
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
        // occurrences across kinds, so the effect's identifier, $once:increment$2, pairs with the recorded increment$2
        // rather than with the first step.
        harness.Workflow = static grain => Changed(grain);
        await using var replay = await harness.ActivateAsync(TestCancellation);

        var response = await replay.GetResponseAsync(WorkflowId, TestCancellation);
        var exception = Assert.IsType<InvalidOperationException>(response.Exception);
        Assert.Contains("'workflow/$once:increment$2'", exception.Message, StringComparison.Ordinal);
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
}
