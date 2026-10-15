using Orleans.DurableTasks;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests.FaultInjection;

/// <summary>
/// Explores single crashes systematically: for every journal write which a representative workflow performs on each
/// participating grain, a run fails that write, either before it is durable or after it is durable with its acknowledgement
/// lost, and the grain recovers from its journal.
/// </summary>
/// <remarks>
/// <para>
/// Each run uses fresh grains and arms one fault on the K-th journal write of one grain, for every K below the longest run
/// observed, including the extra writes of recovery. Each run settles once its grains hold no messaging work, so the
/// writes which follow the result, such as completion retirements, are explored too. Every run must produce the fault-free
/// result, apply each <see cref="DurableTask.Once(string, Func{int})"/> effect exactly once in the grain's journaled state,
/// and leave no dead letters.
/// </para>
/// <para>
/// A failed write fences the grain's journal and deactivates the grain; an ambiguous write is the same failure after the
/// write became durable, which models a crash after the commit point. The harness retries the client's calls, which an
/// activation failure fails, with the same task ID. Only the client calls the workflow's grain: every other grain resumes
/// through its messages and wake-ups, so a run which stalls is a violation.
/// </para>
/// </remarks>
public abstract class DurableTaskCrashExplorationTests(string journalFormatKey) : IAsyncLifetime
{
    private readonly DurableTaskFaultHarness _harness = new(journalFormatKey);

    public ValueTask InitializeAsync() => _harness.InitializeAsync();
    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    [Theory]
    [InlineData(FaultWorkflow.Pipeline)]
    [InlineData(FaultWorkflow.RaceAndCompensate)]
    public async Task FaultFreeRunProducesTheExpectedResultAndEffects(FaultWorkflow workflow)
    {
        var run = await FaultScenario.RunAsync(_harness, workflow);

        Assert.Empty(run.Violations);
    }

    [Theory]
    [InlineData(FaultWorkflow.Pipeline, FaultPoint.JournalWriteBeforeDurable)]
    [InlineData(FaultWorkflow.Pipeline, FaultPoint.JournalWriteAfterDurable)]
    [InlineData(FaultWorkflow.RaceAndCompensate, FaultPoint.JournalWriteBeforeDurable)]
    [InlineData(FaultWorkflow.RaceAndCompensate, FaultPoint.JournalWriteAfterDurable)]
    public async Task EveryJournalWriteFailureOnlyDelaysCompletion(FaultWorkflow workflow, FaultPoint point)
    {
        var baseline = await FaultScenario.RunAsync(_harness, workflow);
        Assert.Empty(baseline.Violations);
        var violations = new List<string>();
        var injected = new Dictionary<FaultRole, int>();
        foreach (var role in Enum.GetValues<FaultRole>())
        {
            // Messages which arrive together can share a write, so runs differ by a write or so. The sweep tries every
            // write index below the longest run observed, including runs whose fault shifted later writes.
            var longest = baseline.Writes[role];
            injected[role] = 0;
            for (var occurrence = 0; occurrence < longest; occurrence++)
            {
                var run = await FaultScenario.RunAsync(_harness, workflow, new FaultSpec(point, role, occurrence));
                longest = Math.Max(longest, run.Writes[role]);
                if (!run.Fired)
                {
                    continue;
                }

                injected[role]++;
                violations.AddRange(run.Violations.Select(violation => $"{role} write #{occurrence}: {violation}"));
            }

            Assert.True(injected[role] > 0, $"No fault fired on the {role}.");
        }

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{workflow} {point}: baseline writes {string.Join(", ", baseline.Writes)}; injected {string.Join(", ", injected)}.");
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }
}

/// <summary>Runs crash exploration with task state stored in the default JSON journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class JsonDurableTaskCrashExplorationTests() : DurableTaskCrashExplorationTests(DurableTaskRecoveryFixture.Json);

/// <summary>Runs crash exploration with task state stored in the Orleans binary journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class OrleansBinaryDurableTaskCrashExplorationTests() : DurableTaskCrashExplorationTests(DurableTaskRecoveryFixture.OrleansBinary);
