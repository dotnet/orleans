using CsCheck;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests.FaultInjection;

/// <summary>
/// Runs the representative workflows under random schedules of several infrastructure faults at every layer: journal
/// writes, ambiguous commits, and reads; outbox deliveries which throw, lose their response, or hang; inbox sinks and
/// handlers which throw; and failed messaging wake-ups.
/// </summary>
/// <remarks>
/// Each schedule must leave the safety contract intact: the fault-free result, every effect exactly once, and no dead
/// letters. CsCheck shrinks a failing schedule to a minimal one and reports its seed, which reproduces it.
/// </remarks>
public abstract class DurableTaskFaultSchedulePropertyTests(string journalFormatKey) : IAsyncLifetime
{
    private const string Invoke = "durable-rpc/invoke";
    private const string Complete = "durable-rpc/complete";
    private const string Signal = "durable-rpc/signal";
    private const string Resume = "durable-rpc/resume";
    private readonly DurableTaskFaultHarness _harness = new(journalFormatKey);

    public ValueTask InitializeAsync() => _harness.InitializeAsync();
    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    private static readonly Gen<FaultSpec> Fault = Gen.OneOf(
        Gen.Select(Gen.OneOfConst(FaultPoint.JournalWriteBeforeDurable, FaultPoint.JournalWriteAfterDurable), Gen.Enum<FaultRole>(), Gen.Int[0, 14])
            .Select(static (point, role, occurrence) => new FaultSpec(point, role, occurrence)),
        Gen.Select(Gen.Enum<FaultRole>(), Gen.Int[0, 1])
            .Select(static (role, occurrence) => new FaultSpec(FaultPoint.JournalRead, role, occurrence)),
        Gen.Select(
                Gen.OneOfConst(FaultPoint.DeliveryThrows, FaultPoint.DeliveryResponseLost, FaultPoint.DeliveryHangs, FaultPoint.InboxSinkThrows),
                Gen.OneOfConst((FaultRole.Partner, Invoke), (FaultRole.Workflow, Complete), (FaultRole.Workflow, Signal)),
                Gen.Int[0, 2])
            .Select(static (point, target, occurrence) => new FaultSpec(point, target.Item1, occurrence, target.Item2)),
        Gen.Int[0, 1].Select(static occurrence => new FaultSpec(FaultPoint.InboxHandlerThrows, FaultRole.Workflow, occurrence, Resume)),
        Gen.Select(Gen.Enum<FaultRole>(), Gen.Int[0, 2])
            .Select(static (role, occurrence) => new FaultSpec(FaultPoint.JobWakeupFails, role, occurrence)));

    private static readonly Gen<(FaultWorkflow Workflow, FaultSpec[] Faults)> Schedule =
        Gen.Select(Gen.Enum<FaultWorkflow>(), Fault.Array[1, 3]);

    [Fact]
    public async Task RandomFaultSchedulesOnlyDelayCompletion()
    {
        var stalls = new List<string>();
        await Schedule.SampleAsync(
            async schedule =>
            {
                var run = await FaultScenario.RunAsync(_harness, schedule.Workflow, schedule.Faults);
                Assert.True(run.Violations.Count == 0, string.Join(Environment.NewLine, run.Violations));
                if (run.Stalled)
                {
                    stalls.Add($"{schedule.Workflow}: {string.Join(", ", schedule.Faults)}");
                }
            },
            iter: 20,
            threads: 1,
            print: static schedule => $"{schedule.Workflow}: {string.Join(", ", schedule.Faults)}");

        FaultScenario.AssertNoStalls(stalls);
    }
}

/// <summary>Runs random fault schedules with task state stored in the default JSON journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class JsonDurableTaskFaultSchedulePropertyTests() : DurableTaskFaultSchedulePropertyTests(DurableTaskRecoveryFixture.Json);

/// <summary>Runs random fault schedules with task state stored in the Orleans binary journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class OrleansBinaryDurableTaskFaultSchedulePropertyTests() : DurableTaskFaultSchedulePropertyTests(DurableTaskRecoveryFixture.OrleansBinary);
