using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests.FaultInjection;

/// <summary>
/// Verifies that a grain which loses its activation while one of its executions is runnable, running a local step rather
/// than waiting for a message, resumes the execution without a call to that grain: its durable wake-up reactivates it.
/// </summary>
/// <remarks>
/// A workflow calls the partner's gated step, which waits for a gate the test opens. The partner's activation ends while the
/// step runs: it deactivates, its silo shuts down or crashes, or its journal write fails. Only the client polls the
/// workflow's grain, and the Durable Jobs clock follows real time, as the wake-ups do.
/// </remarks>
public abstract class DurableTaskRunnableWakeupTests(string journalFormatKey) : IAsyncLifetime
{
    private const int Value = 5;


    // Durable Jobs adopts a crashed silo's shards, which hold the crashed activations' wake-ups, in its periodic shard
    // check: the sweep which a membership change triggers runs before the crashed silo is declared dead.
    private InjectedFault? _wakeups;
    private readonly DurableTaskFaultHarness _harness = new(
        journalFormatKey,
        silo => silo.Services.Configure<DurableJobsOptions>(options => options.ShardCheckInterval = TimeSpan.FromSeconds(1)));

    public ValueTask InitializeAsync() => _harness.InitializeAsync();
    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DeactivationWhileAStepRunsResumesTheStep()
    {
        var (workflow, partnerKey, partner) = await StartGatedStepAsync(silo: null);
        Assert.True(_harness.Fixture.Cluster.TryGetGrainContext(partner.GetGrainId(), out var activation));

        await partner.DeactivateAsync();
        await activation.Deactivated.WaitAsync(DurableTaskFaultHarness.RunTimeout, CancellationToken);
        _harness.Gates.Open(partnerKey);

        await AssertCompletesAsync(workflow, partner);
    }

    [Fact]
    public async Task SiloShutdownWhileAStepRunsResumesTheStepOnAnotherSilo()
    {
        var silo = _harness.Fixture.Cluster.Silos[^1];
        var (workflow, partnerKey, partner) = await StartGatedStepAsync(silo);

        await _harness.Fixture.Cluster.StopSiloAsync(silo, CancellationToken);
        _harness.Gates.Open(partnerKey);

        await AssertCompletesAsync(workflow, partner);
    }

    [Fact]
    public async Task SiloCrashWhileAStepRunsResumesTheStepOnAnotherSilo()
    {
        var silo = _harness.Fixture.Cluster.Silos[^1];
        var (workflow, partnerKey, partner) = await StartGatedStepAsync(silo);

        await _harness.Fixture.Cluster.KillSiloAsync(silo, CancellationToken);
        await _harness.Fixture.Cluster.WaitForLivenessToStabilizeAsync(didKill: true);
        _harness.Gates.Open(partnerKey);

        await AssertCompletesAsync(workflow, partner);
    }

    /// <summary>The step's write fails, before or after it is durable, which fences the partner's journal and deactivates it.</summary>
    [Theory]
    [InlineData(FaultPoint.JournalWriteBeforeDurable)]
    [InlineData(FaultPoint.JournalWriteAfterDurable)]
    public async Task FailedWriteWhileAStepRunsResumesTheStep(FaultPoint point)
    {
        var (workflow, partnerKey, partner) = await StartGatedStepAsync(silo: null);
        var writes = _harness.Injector.Count(FaultPoint.JournalWriteBeforeDurable, partner.GetGrainId());
        await _harness.SettleAsync([writes], CancellationToken);
        var fault = _harness.Injector.Arm(point, partner.GetGrainId(), 0);

        _harness.Gates.Open(partnerKey);

        await AssertCompletesAsync(workflow, partner);
        Assert.True(fault.HasFired, $"{fault} didn't fire.");
    }

    /// <summary>
    /// Starts the workflow's call to the partner's gated step, on <paramref name="silo"/> when provided, and waits until the
    /// step runs.
    /// </summary>
    private async Task<(IFaultWorkflowGrain Workflow, Guid PartnerKey, IFaultPartnerGrain Partner)> StartGatedStepAsync(InProcessSiloHandle? silo)
    {
        _harness.Injector.Reset();
        var workflow = _harness.Client.GetGrain<IFaultWorkflowGrain>(Guid.NewGuid());
        var (partnerKey, partner) = silo is null ? NewPartner() : await ActivatePartnerOnAsync(silo);
        _wakeups = _harness.Injector.Count(FaultPoint.JobWakeupFails, partner.GetGrainId());
        await _harness.CallAsync(token => workflow.CallGatedAsync(partnerKey, Value).WithId("gated").ScheduleAsync(token), CancellationToken);
        await _harness.Fixture.MessagingProbe.WaitForStartedAsync(partnerKey, 1).WaitAsync(DurableTaskFaultHarness.RunTimeout, CancellationToken);
        return (workflow, partnerKey, partner);
    }


    private (Guid Key, IFaultPartnerGrain Grain) NewPartner()
    {
        var key = Guid.NewGuid();
        return (key, _harness.Client.GetGrain<IFaultPartnerGrain>(key));
    }

    /// <summary>Activates fresh partners until placement puts one on <paramref name="silo"/>.</summary>
    private async Task<(Guid Key, IFaultPartnerGrain Grain)> ActivatePartnerOnAsync(InProcessSiloHandle silo)
    {
        await _harness.Fixture.Cluster.WaitForLivenessToStabilizeAsync();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var (key, partner) = NewPartner();
            await partner.GetStateAsync();
            if (_harness.Fixture.Cluster.TryGetGrainContext(partner.GetGrainId(), out var context)
                && silo.SiloAddress.Equals(context.Address.SiloAddress))
            {
                return (key, partner);
            }
        }

        throw new InvalidOperationException($"No partner was placed on {silo.SiloAddress} in 100 activations.");
    }

    private async Task AssertCompletesAsync(IFaultWorkflowGrain workflow, IFaultPartnerGrain partner)
    {
        DurableTaskResponse response;
        try
        {
            response = await _harness.RunAsync<int>(workflow, "gated", static _ => Task.CompletedTask, CancellationToken, advanceClock: false);
        }
        catch (TimeoutException exception)
        {
            var cluster = _harness.Fixture.Cluster;
            var partnerActive = cluster.TryGetGrainContext(partner.GetGrainId(), out var partnerContext);
            throw new TimeoutException(
                $"{exception.Message} Partner active: {partnerActive} on {partnerContext?.Address.SiloAddress}; "
                + $"active silos: {string.Join(", ", cluster.GetActiveSilos().Select(static silo => silo.SiloAddress))}; "
                + $"partner step starts: {_harness.Fixture.MessagingProbe.GetStartedCount(partner.GetPrimaryKey())}; "
                + $"partner wake-ups: {_wakeups?.Matches}; "
                + $"workflow tasks: {await FaultScenario.DescribeTasksAsync(_harness, workflow)}; "
                + $"partner tasks: {await FaultScenario.DescribeTasksAsync(_harness, partner)}.",
                exception);
        }

        Assert.Equal(Value + 1, response.GetResult<int>());
        var violations = await FaultScenario.CheckGrainAsync(_harness, "partner", partner, new() { ["gated"] = 1 });
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }
}

/// <summary>Runs runnable-execution wake-up coverage with task state stored in the default JSON journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class JsonDurableTaskRunnableWakeupTests() : DurableTaskRunnableWakeupTests(DurableTaskRecoveryFixture.Json);

/// <summary>Runs runnable-execution wake-up coverage with task state stored in the Orleans binary journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class OrleansBinaryDurableTaskRunnableWakeupTests() : DurableTaskRunnableWakeupTests(DurableTaskRecoveryFixture.OrleansBinary);
