using Orleans.Configuration;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Diagnostics;
using Orleans.DurableTasks.Protocol;
using Orleans.Messaging;
using Orleans.TestingHost.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests.FaultInjection;

/// <summary>
/// Runs a root task ID again once its grain deleted the earlier run at the end of its result retention, well within
/// Messaging's deduplication window, with faults injected across the later run, and checks that the later run completes
/// with its own result while no message of the earlier run affects it.
/// </summary>
/// <remarks>
/// <para>
/// The root calls the partner, which signals the root back, waits for that signal, and races a remote gated step against
/// a signal from the client and a long delay. Both runs send the same request and the same signal values, so only each
/// run's incarnation, which the identities of its messages cover, keeps the later run's messages from being deduplicated
/// against the earlier run's.
/// </para>
/// <para>
/// Three kinds of message from the earlier run reach the grains during the later run:
/// </para>
/// <list type="bullet">
/// <item>
/// The earlier run's cancellation of its gated step, which lost the race, arrives once the step completed regardless and the
/// partner deleted it, so the partner records it for an invocation which hasn't arrived yet.
/// </item>
/// <item>
/// The resume of the earlier run's delay, which the race canceled, becomes due while the later run's delay with the same
/// identifier and generation is pending.
/// </item>
/// <item>
/// The network delivers copies of the earlier run's invocation and completion of the gated step, and of the partner's
/// signal, which Messaging recognizes as duplicates.
/// </item>
/// </list>
/// <para>
/// The client opens the later run's gate only once each of them was processed, so the later run's step wins its race only
/// if none of them canceled it, completed it, or ended its delay. Its result differs from the earlier run's by the run's
/// number, which an effect counts.
/// </para>
/// </remarks>
public abstract class DurableTaskIncarnationFaultTests(string journalFormatKey) : IAsyncLifetime
{
    private const string Invoke = "durable-rpc/invoke";
    private const string Complete = "durable-rpc/complete";
    private const string Signal = "durable-rpc/signal";
    private const string Cancel = "durable-rpc/cancel";
    private const string Resume = "durable-rpc/resume";
    private const string RootId = "rerun";
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Timeout = DurableTaskFaultHarness.RunTimeout;

    private readonly DurableTaskFaultHarness _harness = new(
        journalFormatKey,
        silo => silo.Services.Configure<DurableTaskOptions>(options => options.ResultRetentionPeriod = Retention));

    public ValueTask InitializeAsync() => _harness.InitializeAsync();
    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public Task LaterRunCompletesWithItsOwnResult() => RunTwiceAsync();

    /// <summary>A delivery or journal fault in the later run only delays its completion.</summary>
    /// <remarks>
    /// The partner's second invocation and the workflow's second completion are the gated step's; the workflow's first
    /// write schedules the later run and deletes the earlier one, and the partner's first write accepts its process call.
    /// </remarks>
    [Theory]
    [InlineData(FaultPoint.DeliveryThrows, FaultRole.Partner, 1, Invoke)]
    [InlineData(FaultPoint.DeliveryResponseLost, FaultRole.Partner, 1, Invoke)]
    [InlineData(FaultPoint.DeliveryThrows, FaultRole.Workflow, 0, Signal)]
    [InlineData(FaultPoint.DeliveryResponseLost, FaultRole.Workflow, 1, Complete)]
    [InlineData(FaultPoint.JournalWriteBeforeDurable, FaultRole.Workflow, 0, null)]
    [InlineData(FaultPoint.JournalWriteAfterDurable, FaultRole.Workflow, 0, null)]
    [InlineData(FaultPoint.JournalWriteBeforeDurable, FaultRole.Partner, 0, null)]
    [InlineData(FaultPoint.JournalWriteAfterDurable, FaultRole.Partner, 2, null)]
    public Task LaterRunCompletesWithItsOwnResultThroughAFault(FaultPoint point, FaultRole role, int occurrence, string? subject) =>
        RunTwiceAsync(new FaultSpec(point, role, occurrence, subject));

    private async Task RunTwiceAsync(params FaultSpec[] laterRunFaults)
    {
        var workflowKey = Guid.NewGuid();
        var workflow = _harness.Client.GetGrain<IFaultWorkflowGrain>(workflowKey);
        var partnerKey = Guid.NewGuid();
        var partner = _harness.Client.GetGrain<IFaultPartnerGrain>(partnerKey);
        var grains = new Dictionary<FaultRole, IFaultInjectedGrain> { [FaultRole.Workflow] = workflow, [FaultRole.Partner] = partner };
        var workflowId = workflow.GetGrainId();
        var partnerId = partner.GetGrainId();
        var injector = _harness.Injector;
        injector.Reset();

        // The earlier run: the client's signal wins the race, and the partner holds the cancellation of the gated step,
        // which completes once its gate opens. The network keeps copies of the step's invocation and completion, and of
        // the partner's signal.
        var heldCancellation = injector.Arm(FaultPoint.DeliveryHeldAtReceiver, partnerId, 0, Cancel);
        var cancellationDelivered = injector.Count(FaultPoint.DeliveryCompleted, partnerId, Cancel);
        InjectedFault[] copies =
        [
            injector.Arm(FaultPoint.DeliveryDuplicated, partnerId, 1, Invoke),
            injector.Arm(FaultPoint.DeliveryDuplicated, workflowId, 1, Complete),
            injector.Arm(FaultPoint.DeliveryDuplicated, workflowId, 0, Signal),
        ];
        using (var events = DurableTaskEventBarriers.Collect())
        {
            await ScheduleAsync(workflow, partnerKey);
            await WaitForRaceAsync(events, workflowKey, run: 1);
            await _harness.CallAsync(
                async token =>
                {
                    await workflow.SignalDurableTaskAsync(RootId, FaultWorkflowGrain.Go, 1, token);
                    return true;
                },
                CancellationToken);
            await heldCancellation.Fired.WaitAsync(Timeout, CancellationToken);
            _harness.Gates.Open(FaultWorkflowGrain.GetGateKey(workflowKey, 1));
            Assert.Equal("run=1;processed=20;ready=30;winner=1;outcome=1", await GetResultAsync(workflow));

            // The partner deletes the step once the workflow accepted its completion, and then receives its cancellation,
            // which it records for an invocation of the step.
            var step = await events.WaitForCallerChildAsync(partnerId, workflowId, TaskId.CreateRoot(RootId).Child("race").Child("0"));
            await events.WaitForDeletedAsync(partnerId, step);
            heldCancellation.Release();
            await cancellationDelivered.WaitForMatchesAsync(1).WaitAsync(Timeout, CancellationToken);
            Assert.True((await GetTasksAsync(partner)).ContainsKey(step), $"The partner holds no record of the cancellation of '{step}'.");
        }

        Assert.All(copies, copy => Assert.NotNull(copy.Duplicate));

        // The earlier run's retention ends, so the next schedule of the root deletes it and runs the root again.
        _harness.Fixture.JobClock.Advance(Retention + TimeSpan.FromMinutes(1));
        var armed = laterRunFaults
            .Select(fault => injector.Arm(fault.Point, grains[fault.Role].GetGrainId(), fault.Occurrence, fault.Subject))
            .ToList();
        var resumes = injector.Count(FaultPoint.InboxHandlerThrows, workflowId, Resume);
        using (var events = DurableTaskEventBarriers.Collect())
        {
            await ScheduleAsync(workflow, partnerKey);
            var deadline = await WaitForRaceAsync(events, workflowKey, run: 2);

            // The copies of the earlier run's messages arrive while the later run's step and delay are pending.
            foreach (var copy in copies)
            {
                var result = await _harness.CallAsync(
                    async token => await _harness.Client.GetGrain<IMessagingExtension>(copy.Target).DeliverAsync(copy.Duplicate!.Value, token),
                    CancellationToken);
                Assert.True(result.Status == DeliveryStatus.Duplicate, $"The copy of {copy} was answered {result.Status}: {result.Message}");
            }

            // The earlier run's delay was due before the later run's, which stays pending, so its resume is dispatched
            // first. The handler applies a resume in the turn which dispatches it, so a later call observes the outcome.
            _harness.Fixture.JobClock.Advance(FaultWorkflowGrain.RunDeadline - TimeSpan.FromMinutes(1));
            await resumes.WaitForMatchesAsync(1).WaitAsync(Timeout, CancellationToken);
            var delayStatus = (await GetTasksAsync(workflow)).GetValueOrDefault(deadline);
            Assert.True(delayStatus is not "Completed", $"The earlier run's resume completed the later run's delay '{deadline}'.");
            _harness.Gates.Open(FaultWorkflowGrain.GetGateKey(workflowKey, 2));
            Assert.Equal("run=2;processed=21;ready=30;winner=0;outcome=4", await GetResultAsync(workflow));
        }

        Assert.All(armed, fault => Assert.True(fault.HasFired, $"{fault} didn't fire."));

        // The later run's delay, which the race canceled, resumes once due, so the grains then hold no messaging work.
        _harness.Fixture.JobClock.Advance(FaultWorkflowGrain.RunDeadline);
        await _harness.SettleAsync([workflowId, partnerId], CancellationToken);
        var violations = await FaultScenario.CheckGrainAsync(_harness, "workflow", workflow, new() { ["run"] = 2 });
        violations.AddRange(await FaultScenario.CheckGrainAsync(_harness, "partner", partner, new() { ["process"] = 2, ["gated"] = 2 }));
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    private Task ScheduleAsync(IFaultWorkflowGrain workflow, Guid partnerKey) =>
        _harness.CallAsync(
            token => workflow.RunAgainAsync(partnerKey, RootId).WithId(RootId).ScheduleAsync(token),
            CancellationToken);

    /// <summary>Waits until the run's gated step runs and its race's delay is recorded, and returns the delay.</summary>
    private async Task<TaskId> WaitForRaceAsync(DiagnosticEventCollector events, Guid workflowKey, int run)
    {
        var workflowId = _harness.Client.GetGrain<IFaultWorkflowGrain>(workflowKey).GetGrainId();
        var race = TaskId.CreateRoot(RootId).Child("race");
        await _harness.Fixture.MessagingProbe
            .WaitForStartedAsync(FaultWorkflowGrain.GetGateKey(workflowKey, run), 1)
            .WaitAsync(Timeout, CancellationToken);
        var created = await events.WaitForEventAsync(
            nameof(DurableTaskEvents.TaskCreated),
            evt => evt.Payload is DurableTaskEvents.TaskCreated payload
                && payload.GrainContext.GrainId == workflowId
                && race.IsAncestorOf(payload.TaskId)
                && payload.TaskId.ToString().EndsWith("/deadline", StringComparison.Ordinal),
            Timeout,
            CancellationToken);
        return ((DurableTaskEvents.TaskCreated)created.Payload!).TaskId;
    }

    private async Task<string> GetResultAsync(IFaultWorkflowGrain workflow)
    {
        var response = await _harness.RunAsync<string>(workflow, RootId, static _ => Task.CompletedTask, CancellationToken);
        Assert.True(
            response.Status == DurableTaskStatus.CompletedSuccessfully,
            $"Status {response.Status}: {response.Exception}; workflow tasks: {await FaultScenario.DescribeTasksAsync(_harness, workflow)}");
        return response.GetResult<string>();
    }

    private async Task<Dictionary<TaskId, string?>> GetTasksAsync(IAddressable grain) =>
        await _harness.CallAsync(async token =>
        {
            var tasks = new Dictionary<TaskId, string?>();
            await foreach (var (taskId, state) in grain.AsReference<IDurableTaskGrainExtension>().GetTasksAsync(token))
            {
                tasks[taskId] = state.Status;
            }

            return tasks;
        }, CancellationToken);
}

/// <summary>Runs root re-run fault coverage with task state stored in the default JSON journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class JsonDurableTaskIncarnationFaultTests() : DurableTaskIncarnationFaultTests(DurableTaskRecoveryFixture.Json);

/// <summary>Runs root re-run fault coverage with task state stored in the Orleans binary journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class OrleansBinaryDurableTaskIncarnationFaultTests() : DurableTaskIncarnationFaultTests(DurableTaskRecoveryFixture.OrleansBinary);
