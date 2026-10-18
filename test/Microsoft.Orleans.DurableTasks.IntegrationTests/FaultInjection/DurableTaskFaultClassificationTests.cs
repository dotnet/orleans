using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests.FaultInjection;

/// <summary>
/// Classifies the outcome of each fault point: an application exception is final and surfaces unchanged, an infrastructure
/// fault only delays completion, and a delivery which fails until Messaging's <c>OutboxRetryTimeout</c> surfaces to the
/// sender as <see cref="DurableTaskDeliveryException"/>.
/// </summary>
/// <remarks>
/// Every run uses fresh grains. Only the client polls the workflow's grain; other grains resume through their messages and
/// wake-ups.
/// </remarks>
public abstract class DurableTaskFaultClassificationTests(string journalFormatKey) : IAsyncLifetime
{
    private const string Invoke = "durable-rpc/invoke";
    private const string Complete = "durable-rpc/complete";
    private const string Signal = "durable-rpc/signal";
    private const string Resume = "durable-rpc/resume";



    private static readonly TimeSpan OutboxRetryTimeout = TimeSpan.FromHours(6);
    private readonly DurableTaskFaultHarness _harness = new(journalFormatKey);

    public ValueTask InitializeAsync() => _harness.InitializeAsync();
    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TaskBodyFailureSurfacesUnchangedToEveryObserverAcrossReactivation()
    {
        var workflow = _harness.Client.GetGrain<IFaultWorkflowGrain>(Guid.NewGuid());

        var response = await _harness.RunAsync<int>(
            workflow, "fail", token => workflow.FailAsync("body failed").WithId("fail").ScheduleAsync(token), CancellationToken);

        AssertApplicationFailure(response, "body failed");
        var awaited = await Assert.ThrowsAsync<FaultApplicationException>(
            async () => await workflow.GetDurableTask<int>("fail").WaitAsync(CancellationToken));
        Assert.Equal("body failed", awaited.Message);
        await ReactivateAsync(workflow);
        AssertApplicationFailure(await workflow.GetDurableTask<int>("fail").GetResponseAsync(CancellationToken), "body failed");
        await AssertGrainAsync("workflow", workflow, new() { ["fail"] = 1 });
    }

    [Fact]
    public async Task LocalStepFailureSurfacesUnchangedToTheBodyAndIsNotRetried()
    {
        var workflow = _harness.Client.GetGrain<IFaultWorkflowGrain>(Guid.NewGuid());

        var result = await RunForResultAsync(workflow, "step", token => workflow.CatchLocalStepFailureAsync("step failed").WithId("step").ScheduleAsync(token));

        Assert.Equal("FaultApplicationException: step failed", result);
        await ReactivateAsync(workflow);
        Assert.Equal(result, (await workflow.GetDurableTask<string>("step").GetResponseAsync(CancellationToken)).GetResult<string>());
        await AssertGrainAsync("workflow", workflow, new() { ["caught"] = 1 });
    }

    [Fact]
    public async Task RunInlineEffectFailureIsRecordedOnceWithItsStateAndSurfacesUnchanged()
    {
        var workflow = _harness.Client.GetGrain<IFaultWorkflowGrain>(Guid.NewGuid());

        var result = await RunForResultAsync(workflow, "once", token => workflow.CatchOnceFailureAsync("effect failed").WithId("once").ScheduleAsync(token));

        Assert.Equal("FaultApplicationException: effect failed", result);
        await ReactivateAsync(workflow);
        Assert.Equal(result, (await workflow.GetDurableTask<string>("once").GetResponseAsync(CancellationToken)).GetResult<string>());
        await AssertGrainAsync("workflow", workflow, new() { ["throwing"] = 1 });
    }

    [Fact]
    public async Task RemoteBodyFailureSurfacesUnchangedToTheCallerAndItsPollers()
    {
        var workflow = _harness.Client.GetGrain<IFaultWorkflowGrain>(Guid.NewGuid());
        var partnerKey = Guid.NewGuid();
        var partner = _harness.Client.GetGrain<IFaultPartnerGrain>(partnerKey);

        var caught = await RunForResultAsync(workflow, "catch", token => workflow.CatchPartnerFailureAsync(partnerKey).WithId("catch").ScheduleAsync(token));
        var propagated = await _harness.RunAsync<int>(
            workflow, "propagate", token => workflow.PropagatePartnerFailureAsync(partnerKey).WithId("propagate").ScheduleAsync(token), CancellationToken);

        Assert.Equal("FaultApplicationException: partner failed", caught);
        AssertApplicationFailure(propagated, "partner failed");
        await ReactivateAsync(workflow);
        AssertApplicationFailure(await workflow.GetDurableTask<int>("propagate").GetResponseAsync(CancellationToken), "partner failed");
        await AssertGrainAsync("workflow", workflow, []);
        await AssertGrainAsync("partner", partner, new() { ["fail"] = 2 });
    }

    [Fact]
    public async Task ConflictingRootIdIsRejectedOnceAndSurfacesUnchangedAcrossReactivation()
    {
        var workflow = _harness.Client.GetGrain<IFaultWorkflowGrain>(Guid.NewGuid());
        var partnerKey = Guid.NewGuid();
        var partner = _harness.Client.GetGrain<IFaultPartnerGrain>(partnerKey);
        await _harness.RunAsync<int>(partner, "taken", token => partner.EchoAsync(1).WithId("taken").ScheduleAsync(token), CancellationToken);

        var result = await RunForResultAsync(workflow, "start", token => workflow.StartDetachedEchoAsync(partnerKey, "taken", 2).WithId("start").ScheduleAsync(token));

        Assert.StartsWith("DurableTaskConflictException: ", result, StringComparison.Ordinal);
        await ReactivateAsync(workflow);
        Assert.Equal(result, (await workflow.GetDurableTask<string>("start").GetResponseAsync(CancellationToken)).GetResult<string>());
        Assert.Equal(2, (await partner.GetDurableTask<int>("taken").GetResponseAsync(CancellationToken)).GetResult<int>());
        Assert.Equal(new Dictionary<string, int> { ["echo"] = 1 }, (await partner.GetStateAsync()).Effects);
    }

    /// <summary>An application failure and an infrastructure fault in one run: the application failure wins, unchanged.</summary>
    [Theory]
    [InlineData(FaultPoint.DeliveryThrows, FaultRole.Partner, 0, Invoke)]
    [InlineData(FaultPoint.DeliveryResponseLost, FaultRole.Partner, 0, Invoke)]
    [InlineData(FaultPoint.DeliveryThrows, FaultRole.Workflow, 0, Complete)]
    [InlineData(FaultPoint.DeliveryResponseLost, FaultRole.Workflow, 0, Complete)]
    [InlineData(FaultPoint.JournalWriteBeforeDurable, FaultRole.Partner, 1, null)]
    [InlineData(FaultPoint.JournalWriteAfterDurable, FaultRole.Partner, 1, null)]
    [InlineData(FaultPoint.JournalWriteBeforeDurable, FaultRole.Workflow, 2, null)]
    [InlineData(FaultPoint.JournalWriteAfterDurable, FaultRole.Workflow, 2, null)]
    public async Task ApplicationFailureWinsOverAnInfrastructureFaultInTheSameRun(FaultPoint point, FaultRole role, int occurrence, string? subject)
    {
        var workflow = _harness.Client.GetGrain<IFaultWorkflowGrain>(Guid.NewGuid());
        var partnerKey = Guid.NewGuid();
        var partner = _harness.Client.GetGrain<IFaultPartnerGrain>(partnerKey);
        _harness.Injector.Reset();
        var fault = _harness.Injector.Arm(point, (role == FaultRole.Workflow ? (IAddressable)workflow : partner).GetGrainId(), occurrence, subject);

        var response = await _harness.RunAsync<int>(
            workflow, "propagate", token => workflow.PropagatePartnerFailureAsync(partnerKey).WithId("propagate").ScheduleAsync(token), CancellationToken);

        Assert.True(fault.HasFired, $"{fault} didn't fire.");
        AssertApplicationFailure(response, "partner failed");
        await AssertGrainAsync("workflow", workflow, []);
        await AssertGrainAsync("partner", partner, new() { ["fail"] = 1 });
    }

    /// <summary>An infrastructure fault at each point of the pipeline only delays its completion.</summary>
    [Theory]
    [InlineData(FaultPoint.DeliveryThrows, FaultRole.Partner, 0, Invoke)]
    [InlineData(FaultPoint.DeliveryThrows, FaultRole.Workflow, 0, Complete)]
    [InlineData(FaultPoint.DeliveryThrows, FaultRole.Workflow, 0, Signal)]
    [InlineData(FaultPoint.DeliveryResponseLost, FaultRole.Partner, 0, Invoke)]
    [InlineData(FaultPoint.DeliveryResponseLost, FaultRole.Workflow, 0, Complete)]
    [InlineData(FaultPoint.DeliveryResponseLost, FaultRole.Workflow, 0, Signal)]
    [InlineData(FaultPoint.DeliveryHangs, FaultRole.Partner, 0, Invoke)]
    [InlineData(FaultPoint.DeliveryHangs, FaultRole.Workflow, 0, Complete)]
    [InlineData(FaultPoint.JournalRead, FaultRole.Workflow, 0, null)]
    [InlineData(FaultPoint.JournalRead, FaultRole.Partner, 0, null)]
    [InlineData(FaultPoint.InboxHandlerThrows, FaultRole.Workflow, 0, Resume)]
    [InlineData(FaultPoint.JobWakeupFails, FaultRole.Workflow, 0, null)]
    public async Task InfrastructureFaultOnlyDelaysCompletion(FaultPoint point, FaultRole role, int occurrence, string? subject)
    {
        var run = await FaultScenario.RunAsync(_harness, FaultWorkflow.Pipeline, new FaultSpec(point, role, occurrence, subject));

        Assert.True(run.Fired, "The fault didn't fire.");
        Assert.True(run.Violations.Count == 0, string.Join(Environment.NewLine, run.Violations));
    }

    /// <summary>A sink which throws is an infrastructure fault: Messaging retries the message, which is accepted once.</summary>
    [Theory]
    [InlineData(FaultRole.Partner, Invoke)]
    [InlineData(FaultRole.Workflow, Complete)]
    [InlineData(FaultRole.Workflow, Signal)]
    public async Task InboxSinkFailureOnlyDelaysCompletion(FaultRole role, string subject)
    {
        var run = await FaultScenario.RunAsync(_harness, FaultWorkflow.Pipeline, new FaultSpec(FaultPoint.InboxSinkThrows, role, 0, subject));

        Assert.True(run.Fired, "The fault didn't fire.");
        Assert.True(run.Violations.Count == 0, string.Join(Environment.NewLine, run.Violations));
    }

    /// <summary>
    /// A delivery which the receiver admitted before a failed write fenced its journal reaches the inbox while the fenced
    /// activation deactivates: the inbox's sink fails to stage it, the sender retries, and the next activation accepts the
    /// message exactly once.
    /// </summary>
    [Theory]
    [InlineData(Invoke)]
    [InlineData(Complete)]
    [InlineData(Signal)]
    public async Task DeliveryToAFencedActivationIsAcceptedOnceByTheNextActivation(string subject)
    {
        var workflow = _harness.Client.GetGrain<IFaultWorkflowGrain>(Guid.NewGuid());
        var partnerKey = Guid.NewGuid();
        var partner = _harness.Client.GetGrain<IFaultPartnerGrain>(partnerKey);
        var fencedKey = Guid.NewGuid();
        var fenced = _harness.Client.GetGrain<IFaultFencedGrain>(fencedKey);
        var fencedId = fenced.GetGrainId();
        _harness.Injector.Reset();
        var hold = _harness.Injector.Arm(FaultPoint.DeliveryHeldAtReceiver, fencedId, 0, subject);

        // The fenced grain admits a delivery and holds it before its inbox sees the message.
        Func<Task<int>> runToCompletion;
        Dictionary<string, int> expectedEffects;
        switch (subject)
        {
            case Invoke:
                await _harness.CallAsync(token => workflow.StartDetachedFencedEchoAsync(fencedKey, "echo", 5).WithId("start").ScheduleAsync(token), CancellationToken);
                runToCompletion = async () => int.Parse((await RunForResultAsync(workflow, "start", static _ => Task.CompletedTask))["echoed ".Length..]);
                expectedEffects = new() { ["echo"] = 1 };
                break;
            case Complete:
                await _harness.CallAsync(token => fenced.CallEchoAsync(partnerKey, 5).WithId("call").ScheduleAsync(token), CancellationToken);
                runToCompletion = async () => (await _harness.RunAsync<int>(fenced, "call", static _ => Task.CompletedTask, CancellationToken)).GetResult<int>();
                expectedEffects = [];
                break;
            default:
                await _harness.CallAsync(token => fenced.WaitForReadyAsync().WithId("wait").ScheduleAsync(token), CancellationToken);
                await _harness.CallAsync(token => partner.SignalFencedAsync(fencedKey, "wait", 6).WithId("send").ScheduleAsync(token), CancellationToken);
                runToCompletion = async () => (await _harness.RunAsync<int>(fenced, "wait", static _ => Task.CompletedTask, CancellationToken)).GetResult<int>();
                expectedEffects = [];
                break;
        }

        await hold.Fired.WaitAsync(DurableTaskFaultHarness.RunTimeout, CancellationToken);

        // The grain's other work, such as its outbox's retirement of a delivered message, finishes first, so the reentrant
        // call's write is the one which fails. The failure fences the journal and requests deactivation, which waits for the
        // held call.
        await _harness.SettleAsync([fencedId], CancellationToken);
        var writeFault = _harness.Injector.Arm(FaultPoint.JournalWriteBeforeDurable, fencedId, 0);
        Assert.StartsWith(InjectedFaultException.MessagePrefix, await fenced.FenceAsync(), StringComparison.Ordinal);
        Assert.True(writeFault.HasFired);
        hold.Release();

        Assert.Equal(6, await runToCompletion());
        await AssertGrainAsync("workflow", workflow, []);
        await AssertGrainAsync("fenced", fenced, expectedEffects);
        await AssertGrainAsync("partner", partner, subject == Complete ? new() { ["echo"] = 1 } : []);
    }

    /// <summary>
    /// A delivery which keeps failing is retried, not dead-lettered, within <c>OutboxRetryTimeout</c>; once it succeeds,
    /// the call's own outcome, here an application failure, surfaces unchanged.
    /// </summary>
    [Fact]
    public async Task FailingDeliveryIsRetriedWithinTheRetryTimeout()
    {
        var workflow = _harness.Client.GetGrain<IFaultWorkflowGrain>(Guid.NewGuid());
        var partnerKey = Guid.NewGuid();
        var partner = _harness.Client.GetGrain<IFaultPartnerGrain>(partnerKey);
        _harness.Injector.Reset();
        var fault = _harness.Injector.Arm(FaultPoint.DeliveryThrows, partner.GetGrainId(), InjectedFault.Every, Invoke);
        await _harness.CallAsync(token => workflow.CatchPartnerFailureAsync(partnerKey).WithId("catch").ScheduleAsync(token), CancellationToken);
        await fault.WaitForMatchesAsync(1).WaitAsync(DurableTaskFaultHarness.RunTimeout, CancellationToken);

        // An attempt after the clock passed most of the retry window fails again, and the message stays pending.
        var attempts = fault.Matches;
        _harness.Fixture.JobClock.Advance(OutboxRetryTimeout - TimeSpan.FromHours(1));
        await fault.WaitForMatchesAsync(attempts + 1).WaitAsync(DurableTaskFaultHarness.RunTimeout, CancellationToken);
        Assert.Empty((await workflow.GetStateAsync()).OutboxDeadLetters);
        _harness.Injector.Reset();

        Assert.Equal("FaultApplicationException: partner failed", await RunForResultAsync(workflow, "catch", static _ => Task.CompletedTask));
        await AssertGrainAsync("workflow", workflow, []);
        await AssertGrainAsync("partner", partner, new() { ["fail"] = 1 });
    }

    /// <summary>
    /// A delivery which fails until <c>OutboxRetryTimeout</c> expires surfaces to the sender as
    /// <see cref="DurableTaskDeliveryException"/>, never as an application exception.
    /// </summary>
    [Fact]
    public async Task DeliveryWhichFailsUntilTheRetryTimeoutFailsTheCallWithDeliveryException()
    {
        var workflow = _harness.Client.GetGrain<IFaultWorkflowGrain>(Guid.NewGuid());
        var partnerKey = Guid.NewGuid();
        var partner = _harness.Client.GetGrain<IFaultPartnerGrain>(partnerKey);
        _harness.Injector.Reset();
        var fault = _harness.Injector.Arm(FaultPoint.DeliveryThrows, partner.GetGrainId(), InjectedFault.Every, Invoke);
        await _harness.CallAsync(token => workflow.CatchPartnerFailureAsync(partnerKey).WithId("catch").ScheduleAsync(token), CancellationToken);
        await fault.WaitForMatchesAsync(1).WaitAsync(DurableTaskFaultHarness.RunTimeout, CancellationToken);

        _harness.Fixture.JobClock.Advance(OutboxRetryTimeout + TimeSpan.FromHours(1));
        var result = await RunForResultAsync(workflow, "catch", static _ => Task.CompletedTask);

        Assert.StartsWith("DurableTaskDeliveryException: ", result, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(FaultApplicationException), result, StringComparison.Ordinal);
        Assert.Single((await workflow.GetStateAsync()).OutboxDeadLetters);
        await AssertGrainAsync("partner", partner, []);
    }

    private async Task<string> RunForResultAsync(IAddressable grain, string rootId, Func<CancellationToken, Task> schedule)
    {
        var response = await _harness.RunAsync<string>(grain, rootId, schedule, CancellationToken);
        Assert.True(response.Status == DurableTaskStatus.CompletedSuccessfully, $"Status {response.Status}: {response.Exception}");
        return response.GetResult<string>();
    }

    private async Task ReactivateAsync(IFaultInjectedGrain grain)
    {
        Assert.True(_harness.Fixture.Cluster.TryGetGrainContext(grain.GetGrainId(), out var activation));
        await grain.DeactivateAsync();
        await activation.Deactivated.WaitAsync(DurableTaskFaultHarness.RunTimeout, CancellationToken);
    }

    private async Task AssertGrainAsync(string name, IFaultInjectedGrain grain, Dictionary<string, int> expectedEffects)
    {
        await _harness.SettleAsync([grain.GetGrainId()], CancellationToken);
        var violations = await FaultScenario.CheckGrainAsync(_harness, name, grain, expectedEffects);
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    private static void AssertApplicationFailure(DurableTaskResponse response, string message)
    {
        Assert.Equal(DurableTaskStatus.Failed, response.Status);
        var exception = Assert.IsType<FaultApplicationException>(response.Exception);
        Assert.Equal(message, exception.Message);
    }
}

/// <summary>Runs fault classification coverage with task state stored in the default JSON journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class JsonDurableTaskFaultClassificationTests() : DurableTaskFaultClassificationTests(DurableTaskRecoveryFixture.Json);

/// <summary>Runs fault classification coverage with task state stored in the Orleans binary journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class OrleansBinaryDurableTaskFaultClassificationTests() : DurableTaskFaultClassificationTests(DurableTaskRecoveryFixture.OrleansBinary);
