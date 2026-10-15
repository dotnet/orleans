using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests.FaultInjection;

/// <summary>A representative workflow which a fault scenario runs.</summary>
public enum FaultWorkflow
{
    /// <summary>A local step, an effect, a call which signals back, a signal wait, a delay, and a detached start.</summary>
    Pipeline,

    /// <summary>A race whose loser compensates through a shielded child, then a signal wait which times out.</summary>
    RaceAndCompensate,
}

/// <summary>A grain which participates in a fault scenario.</summary>
public enum FaultRole
{
    Workflow,
    Partner,
}

/// <summary>A fault to arm on a scenario's grain.</summary>
public sealed record FaultSpec(FaultPoint Point, FaultRole Role, int Occurrence, string? Subject = null)
{
    public override string ToString() => $"{Point} #{Occurrence} on {Role}{(Subject is null ? "" : $" ({Subject})")}";
}

/// <summary>
/// The outcome of a scenario run: whether its faults fired, the writes each grain performed, every violation of the safety
/// contract, and whether the run stalled until the harness activated its grains.
/// </summary>
public sealed record FaultRunOutcome(bool Fired, Dictionary<FaultRole, int> Writes, List<string> Violations, bool Stalled);

/// <summary>
/// Runs a representative workflow on fresh grains with faults armed, and checks the contract: the fault-free result, each
/// effect exactly once in the grains' journaled state, and no dead letters.
/// </summary>
internal static class FaultScenario
{
    public const string RootId = "workflow";


    public static async Task<FaultRunOutcome> RunAsync(DurableTaskFaultHarness harness, FaultWorkflow workflow, params FaultSpec[] faults)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var client = harness.Client;
        var workflowGrain = client.GetGrain<IFaultWorkflowGrain>(Guid.NewGuid());
        var partnerKey = Guid.NewGuid();
        var partnerGrain = client.GetGrain<IFaultPartnerGrain>(partnerKey);
        var grains = new Dictionary<FaultRole, IFaultInjectedGrain> { [FaultRole.Workflow] = workflowGrain, [FaultRole.Partner] = partnerGrain };
        harness.Injector.Reset();
        var writes = grains.ToDictionary(
            static pair => pair.Key,
            pair => harness.Injector.Count(FaultPoint.JournalWriteBeforeDurable, pair.Value.GetGrainId()));
        var armed = faults
            .Select(fault => harness.Injector.Arm(fault.Point, grains[fault.Role].GetGrainId(), fault.Occurrence, fault.Subject))
            .ToList();
        var hangs = armed.Where(static fault => fault.Point == FaultPoint.DeliveryHangs).ToList();
        foreach (var hang in hangs)
        {
            // The hung call times out once it is observed to hang.
            _ = hang.Fired.ContinueWith(_ => hang.Release(), TaskScheduler.Default);
        }

        var (expected, expectedEffects) = GetExpectations(workflow);
        var violations = new List<string>();
        var stalled = false;
        try
        {
            var response = await harness.RunAsync<string>(
                workflowGrain,
                RootId,
                token => workflow == FaultWorkflow.Pipeline
                    ? workflowGrain.PipelineAsync(partnerKey, RootId).WithId(RootId).ScheduleAsync(token)
                    : workflowGrain.RaceAsync(partnerKey, RootId).WithId(RootId).ScheduleAsync(token),
                cancellationToken,
                nudge: token => Task.WhenAll(grains.Values.Select(static grain => grain.GetStateAsync())),
                onStalled: () => stalled = true);
            if (response.Status != DurableTaskStatus.CompletedSuccessfully)
            {
                violations.Add($"status {response.Status}: {response.Exception}");
            }
            else if (response.GetResult<string>() is var result && result != expected)
            {
                violations.Add($"result '{result}', expected '{expected}'");
            }

            // Completions retire and records are deleted after the result, so the run's last writes follow it.
            await harness.SettleAsync([.. writes.Values], cancellationToken);
            foreach (var (role, grain) in grains)
            {
                violations.AddRange(await CheckGrainAsync(harness, role.ToString(), grain, expectedEffects[role]));
            }
        }
        catch (TimeoutException exception)
        {
            violations.Add($"stalled: {exception.Message}");
        }

        return new(
            armed.All(static fault => fault.HasFired),
            writes.ToDictionary(static pair => pair.Key, static pair => pair.Value.Matches),
            violations,
            stalled);
    }

    /// <summary>
    /// Asserts that no run stalled: each completed without the harness activating grains which only their own messages
    /// and wake-ups activate.
    /// </summary>
    /// <remarks>Call it after the safety assertions, which the harness checks for a stalled run once it activated the grains.</remarks>
    public static void AssertNoStalls(IReadOnlyCollection<string> stalls)
    {
        Assert.True(stalls.Count == 0, $"Runs stalled until the harness activated their grains: {string.Join("; ", stalls)}.");
    }

    /// <summary>Returns the violations of a grain's effects and dead letters against the expected effect counts.</summary>
    public static async Task<List<string>> CheckGrainAsync(
        DurableTaskFaultHarness harness,
        string name,
        IFaultInjectedGrain grain,
        Dictionary<string, int> expectedEffects,
        bool allowOutboxDeadLetters = false)
    {
        var violations = new List<string>();
        var state = await harness.CallAsync(_ => grain.GetStateAsync(), TestContext.Current.CancellationToken);
        var effects = Describe(state.Effects);
        var expectedText = Describe(expectedEffects);
        if (effects != expectedText)
        {
            violations.Add($"{name} effects [{effects}], expected [{expectedText}]; tasks: {await DescribeTasksAsync(harness, grain)}");
        }

        violations.AddRange(state.InboxDeadLetters.Select(letter => $"{name} inbox dead letter {letter}"));
        if (!allowOutboxDeadLetters)
        {
            violations.AddRange(state.OutboxDeadLetters.Select(letter => $"{name} outbox dead letter {letter}"));
        }

        return violations;
    }

    /// <summary>Describes each task the grain holds, with its status, response, and request.</summary>
    public static Task<string> DescribeTasksAsync(DurableTaskFaultHarness harness, IAddressable grain) =>
        harness.CallAsync(async cancellationToken =>
        {
            var tasks = new List<string>();
            await foreach (var (taskId, state) in grain.AsReference<IDurableTaskGrainExtension>().GetTasksAsync(cancellationToken))
            {
                tasks.Add($"{taskId}={state.Status}({state.Response}) {state.Request}");
            }

            return string.Join(" | ", tasks);
        }, TestContext.Current.CancellationToken);

    private static string Describe(Dictionary<string, int> effects) =>
        string.Join(",", effects.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(static pair => $"{pair.Key}={pair.Value}"));

    private static (string Result, Dictionary<FaultRole, Dictionary<string, int>> Effects) GetExpectations(FaultWorkflow workflow) =>
        workflow switch
        {
            FaultWorkflow.Pipeline => (
                "step=20;effect=1;processed=40;ready=60;echoed=41",
                new()
                {
                    [FaultRole.Workflow] = new() { ["workflow"] = 1 },
                    [FaultRole.Partner] = new() { ["process"] = 1, ["echo"] = 1 },
                }),
            FaultWorkflow.RaceAndCompensate => (
                "winner=1;outcome=gate after held;deadline=timed-out;effect=1",
                new()
                {
                    [FaultRole.Workflow] = new() { ["race"] = 1 },
                    [FaultRole.Partner] = new() { ["compensate"] = 1 },
                }),
            _ => throw new ArgumentOutOfRangeException(nameof(workflow)),
        };
}
