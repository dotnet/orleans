using System.Distributed.DurableTasks;
using DurableWorkflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.TestingHost;
using Xunit;

namespace DurableWorkflows.Tests;

public sealed class WorkflowTests : IAsyncLifetime
{
    private readonly VolatileJournalStorageProvider _storage = CreateStorage();
    private InProcessTestCluster _cluster = null!;
    private IClusterClient Client => _cluster.Client;

    public async Task InitializeAsync()
    {
        _cluster = CreateCluster(1, _storage);
        await _cluster.DeployAsync();
    }

    public Task DisposeAsync() => _cluster.DisposeAsync().AsTask();

    [Fact]
    public async Task BasicWorkflowAndDuplicateReplayExecuteEffectsOnce()
    {
        var id = UniqueId();
        var workflow = Client.GetGrain<IWorkflowGrain>(id);

        var first = await RunAsync(workflow.RunBasicAsync("hello"), $"basic-{id}");
        var replay = await RunAsync(workflow.RunBasicAsync("hello"), $"basic-{id}");

        Assert.Equal("HELLO", first.Output);
        Assert.Equal(first, replay);
        Assert.Single((await Client.GetGrain<IActivityGrain>($"{id}-validate").GetSnapshotAsync()).Effects);
        Assert.Single((await Client.GetGrain<IActivityGrain>($"{id}-store").GetSnapshotAsync()).Effects);
    }

    [Fact]
    public async Task FanOutFanInCompletesEveryDurableBranch()
    {
        var id = UniqueId();
        var items = new[] { "one", "two", "three" };

        var result = await RunAsync(
            Client.GetGrain<IWorkflowGrain>(id).RunFanOutAsync(items),
            $"fanout-{id}");

        Assert.Equal(items.Length, result.CompletedCount);
        Assert.Equal(items.Length, result.TaskIds.Length);
        Assert.Equal(items.Length, result.TaskIds.Distinct(StringComparer.Ordinal).Count());
        for (var index = 0; index < items.Length; index++)
        {
            var snapshot = await Client.GetGrain<IActivityGrain>($"{id}-fanout-{index}").GetSnapshotAsync();
            Assert.Equal(items[index].ToUpperInvariant(), Assert.Single(snapshot.Effects).Value.Output);
        }
    }

    [Theory]
    [InlineData(true, "approved")]
    [InlineData(false, "rejected")]
    public async Task HumanDecisionUsesStableCorrelationIdentity(bool approved, string reason)
    {
        var correlationId = UniqueId();
        var workflow = Client.GetGrain<IWorkflowGrain>(correlationId);
        var scheduled = await workflow.RunApprovalAsync(new(correlationId, "deploy"))
            .ScheduleAsync($"approval-{correlationId}");

        await Client.GetGrain<IApprovalGrain>(correlationId)
            .SubmitDecisionAsync(new(correlationId, approved, reason));
        var result = await scheduled.WaitAsync();

        Assert.Equal(correlationId, result.CorrelationId);
        Assert.Equal(approved, result.Approved);
        Assert.Equal(reason, result.Reason);
        Assert.Equal(correlationId, (await Client.GetGrain<IApprovalGrain>(correlationId).GetSnapshotAsync()).CorrelationId);
    }

    [Fact]
    public async Task CancellationPersistsAcrossDeactivation()
    {
        var id = UniqueId();
        var workflow = Client.GetGrain<IWorkflowGrain>(id);
        var scheduled = await workflow.RunCancellationAsync(id).ScheduleAsync($"cancel-{id}");
        Assert.Equal(DurableTaskStatus.Pending, await scheduled.GetStatusAsync());

        await _cluster.DeactivateAsync(workflow);
        await Client.GetGrain<ICancellationGrain>(id)
            .RequestCancellationAsync(new(id, "operator request"));
        var recovered = await workflow.RunCancellationAsync(id).ScheduleAsync($"cancel-{id}");
        var result = await recovered.WaitAsync();

        Assert.True(result.Canceled);
        Assert.Equal("operator request", result.Reason);
        Assert.Equal(id, (await Client.GetGrain<ICancellationGrain>(id).GetSnapshotAsync()).Signal!.CancellationId);
    }

    [Fact]
    public async Task FailedSagaCompensatesInReverseOrderAndCompensationIsIdempotent()
    {
        var orderId = UniqueId();
        var workflow = Client.GetGrain<IWorkflowGrain>(orderId);
        var request = new OrderRequest(orderId, FailShipping: true);

        var rootId = $"order-{orderId}-first";
        var first = await RunAsync(workflow.RunOrderSagaAsync(request), rootId);
        var replay = await RunAsync(workflow.RunOrderSagaAsync(request), rootId);
        var duplicateWorkflow = await RunAsync(workflow.RunOrderSagaAsync(request), $"order-{orderId}-second");

        Assert.Equal(OrderWorkflowStatus.Compensated, first.Status);
        Assert.Equal(new[] { "refund-payment", "release-inventory" }, first.Compensations);
        Assert.Equal(first.OrderId, replay.OrderId);
        Assert.Equal(first.Status, replay.Status);
        Assert.Equal(first.Compensations, replay.Compensations);
        Assert.Equal(first.Failure, replay.Failure);
        Assert.Equal(first.Compensations, duplicateWorkflow.Compensations);

        var inventory = await Client.GetGrain<IInventoryGrain>(orderId).GetSnapshotAsync();
        var payment = await Client.GetGrain<IPaymentGrain>(orderId).GetSnapshotAsync();
        Assert.Equal(2, inventory.Effects.Count);
        Assert.Equal(2, payment.Effects.Count);
        Assert.Single(inventory.Effects.Values, effect => effect.Kind == "release-inventory");
        Assert.Single(payment.Effects.Values, effect => effect.Kind == "refund-payment");
    }

    [Fact]
    public async Task SuccessfulSagaDoesNotCompensate()
    {
        var orderId = UniqueId();
        var result = await RunAsync(
            Client.GetGrain<IWorkflowGrain>(orderId).RunOrderSagaAsync(new(orderId, FailShipping: false)),
            $"order-{orderId}");

        Assert.Equal(OrderWorkflowStatus.Completed, result.Status);
        Assert.Empty(result.Compensations);
    }

    private static async Task<TResult> RunAsync<TResult>(DurableTask<TResult> definition, string rootId)
    {
        var scheduled = await definition.ScheduleAsync(rootId);
        return await scheduled.WaitAsync();
    }

    private static string UniqueId() => Guid.NewGuid().ToString("N");

    internal static VolatileJournalStorageProvider CreateStorage() =>
        new(Options.Create(new JournaledStateManagerOptions { JournalFormatKey = "orleans-binary" }));

    internal static InProcessTestCluster CreateCluster(short silos, VolatileJournalStorageProvider storage)
    {
        var builder = new InProcessTestClusterBuilder(silos);
        builder.ConfigureClient(clientBuilder => clientBuilder.AddDurableTasks());
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            siloBuilder
                .UseInMemoryDurableJobs()
                .AddDurableTasks(options => options.ResultRetentionPeriod = TimeSpan.FromHours(1))
                .Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = "orleans-binary");
            siloBuilder.Services.RemoveAll<IJournalStorageProvider>();
            siloBuilder.Services.RemoveAll<IJournalStorageCatalog>();
            siloBuilder.Services.AddSingleton<IJournalStorageProvider>(storage);
            siloBuilder.Services.AddSingleton<IJournalStorageCatalog>(storage);
        });
        return builder.Build();
    }
}

public sealed class WorkflowRecoveryTests
{
    [Fact]
    public async Task InProgressWorkflowRecoversOnAnotherSiloAfterReplicaTermination()
    {
        var storage = WorkflowTests.CreateStorage();
        await using var cluster = WorkflowTests.CreateCluster(2, storage);
        await cluster.DeployAsync();
        var id = Guid.NewGuid().ToString("N");
        var workflow = cluster.Client.GetGrain<IWorkflowGrain>(id);
        var request = new ApprovalRequest(id, "recover-after-failover");
        var scheduled = await workflow.RunApprovalAsync(request).ScheduleAsync($"recovery-{id}");
        var initial = await scheduled.GetResponseAsync(new PollingOptions { PollTimeout = TimeSpan.Zero });
        Assert.True(
            initial.Status is DurableTaskStatus.Pending,
            $"Expected a pending workflow, got {initial.Status}: {initial.Exception}");

        var before = await workflow.GetRuntimeInfoAsync();
        var victim = cluster.GetActiveSilos().Single(silo =>
            string.Equals(silo.SiloAddress.ToParsableString(), before.SiloAddress, StringComparison.Ordinal));
        await cluster.KillSiloAsync(victim);
        await cluster.WaitForLivenessToStabilizeAsync(didKill: true);

        await cluster.Client.GetGrain<IApprovalGrain>(id)
            .SubmitDecisionAsync(new(id, Approved: true, "completed after failover"));
        var recovered = await workflow.RunApprovalAsync(request).ScheduleAsync($"recovery-{id}");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await recovered.WaitAsync(timeout.Token);
        var after = await workflow.GetRuntimeInfoAsync();

        Assert.NotEqual(before.SiloAddress, after.SiloAddress);
        Assert.NotEqual(before.ActivationId, after.ActivationId);
        Assert.Equal(after.SiloAddress, result.SiloAddress);
    }
}
