using Microsoft.Extensions.DependencyInjection;
using Microsoft.Orleans.DurableTasks.IntegrationTests.FaultInjection;
using Orleans.DurableTasks;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests;

/// <summary>
/// Verifies that a client which waits for a task keeps waiting across failures to reach the task's grain, such as a crash
/// of the grain's silo or a deactivation of the grain, and receives the task's result once the grain recovers.
/// </summary>
public abstract class DurableTaskClientWaitTests(string journalFormatKey) : IAsyncLifetime
{
    private const int Value = 5;
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(60);

    // Durable Jobs adopts a crashed silo's shards, which hold the crashed activations' wake-ups, in its periodic shard check.
    private readonly DurableTaskFaultHarness _harness = new(
        journalFormatKey,
        silo => silo.Services.Configure<DurableJobsOptions>(options => options.ShardCheckInterval = TimeSpan.FromSeconds(1)));

    public ValueTask InitializeAsync() => _harness.InitializeAsync();
    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ClientWaitCompletesAfterTheGrainsSiloCrashes()
    {
        var silo = _harness.Fixture.Cluster.Silos[^1];
        var (key, partner) = await ActivatePartnerOnAsync(silo);
        var waiting = await StartWaitAsync(key, partner);

        await _harness.Fixture.Cluster.KillSiloAsync(silo, CancellationToken);
        await _harness.Fixture.Cluster.WaitForLivenessToStabilizeAsync(didKill: true);
        _harness.Gates.Open(key);

        Assert.Equal(Value + 1, (await waiting.WaitAsync(WaitTimeout, CancellationToken)).GetResult<int>());
    }

    [Fact]
    public async Task ClientWaitCompletesAfterTheGrainDeactivates()
    {
        var key = Guid.NewGuid();
        var partner = _harness.Client.GetGrain<IFaultPartnerGrain>(key);
        var waiting = await StartWaitAsync(key, partner);
        Assert.True(_harness.Fixture.Cluster.TryGetGrainContext(partner.GetGrainId(), out var activation));

        await partner.DeactivateAsync();
        await activation.Deactivated.WaitAsync(WaitTimeout, CancellationToken);
        _harness.Gates.Open(key);

        Assert.Equal(Value + 1, (await waiting.WaitAsync(WaitTimeout, CancellationToken)).GetResult<int>());
    }

    /// <summary>Starts the partner's gated step and a client's wait for it, and returns the wait once the step runs.</summary>
    private async Task<Task<DurableTaskResponse>> StartWaitAsync(Guid key, IFaultPartnerGrain partner)
    {
        await partner.GatedStepAsync(Value).WithId("gated").ScheduleAsync(CancellationToken);
        await _harness.Fixture.MessagingProbe.WaitForStartedAsync(key, 1).WaitAsync(WaitTimeout, CancellationToken);
        var waiting = partner.GetDurableTask<int>("gated").GetResponseAsync(CancellationToken);
        Assert.False(waiting.IsCompleted);
        return waiting;
    }

    /// <summary>Activates fresh partners until placement puts one on <paramref name="silo"/>.</summary>
    private async Task<(Guid Key, IFaultPartnerGrain Grain)> ActivatePartnerOnAsync(InProcessSiloHandle silo)
    {
        await _harness.Fixture.Cluster.WaitForLivenessToStabilizeAsync();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var key = Guid.NewGuid();
            var partner = _harness.Client.GetGrain<IFaultPartnerGrain>(key);
            await partner.GetStateAsync();
            if (_harness.Fixture.Cluster.TryGetGrainContext(partner.GetGrainId(), out var context)
                && silo.SiloAddress.Equals(context.Address.SiloAddress))
            {
                return (key, partner);
            }
        }

        throw new InvalidOperationException($"No partner was placed on {silo.SiloAddress} in 100 activations.");
    }
}

/// <summary>Runs client wait coverage with task state stored in the default JSON journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class JsonDurableTaskClientWaitTests() : DurableTaskClientWaitTests(DurableTaskRecoveryFixture.Json);

/// <summary>Runs client wait coverage with task state stored in the Orleans binary journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class OrleansBinaryDurableTaskClientWaitTests() : DurableTaskClientWaitTests(DurableTaskRecoveryFixture.OrleansBinary);
