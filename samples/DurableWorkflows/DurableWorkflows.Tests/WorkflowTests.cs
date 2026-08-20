using System.Distributed.DurableTasks;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DurableWorkflows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
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
    public async Task DecisionRecordedBeforeWorkflowWaitIsRetained()
    {
        var correlationId = UniqueId();
        var decision = new ApprovalDecision(correlationId, Approved: true, "recorded first");
        await Client.GetGrain<IApprovalGrain>(correlationId).SubmitDecisionAsync(decision);

        var result = await RunAsync(
            Client.GetGrain<IWorkflowGrain>(correlationId).RunApprovalAsync(new(correlationId, "deploy")),
            $"approval-{correlationId}");

        Assert.True(result.Approved);
        Assert.Equal("recorded first", result.Reason);
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

public sealed class WorkflowEndpointTests : IAsyncLifetime
{
    private readonly VolatileJournalStorageProvider _storage = WorkflowTests.CreateStorage();
    private InProcessTestCluster _cluster = null!;
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _cluster = WorkflowTests.CreateCluster(1, _storage);
        await _cluster.DeployAsync();

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IClusterClient>(_cluster.Client);
        _app = builder.Build();
        _app.MapDurableWorkflowEndpoints();
        await _app.StartAsync();

        var server = _app.Services.GetRequiredService<IServer>();
        var address = Assert.Single(server.Features.Get<IServerAddressesFeature>()!.Addresses);
        _http = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
        await _cluster.DisposeAsync();
    }

    [Theory]
    [InlineData(true, "approved")]
    [InlineData(false, "rejected")]
    public async Task ApprovalResourceTransitionsFromPendingToTerminal(bool approved, string reason)
    {
        var id = UniqueId();
        var expectedLocation = $"/workflows/approval/{id}/status";

        using var start = await _http.PostAsync($"/workflows/approval/{id}?subject=production", null);
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        Assert.Equal(expectedLocation, start.Headers.Location!.OriginalString);
        var accepted = await start.Content.ReadFromJsonAsync<AcceptedDocument>();
        Assert.Equal(expectedLocation, accepted!.StatusUrl);

        var pending = await GetStatusAsync(expectedLocation);
        Assert.Equal("pending", pending.Status);
        Assert.Equal(accepted.TaskId, pending.TaskId);

        using var submit = await _http.PutAsJsonAsync(
            $"/workflows/approval/{id}",
            new ApprovalSubmission("production", approved, reason));
        Assert.Equal(HttpStatusCode.Accepted, submit.StatusCode);
        Assert.Equal(expectedLocation, submit.Headers.Location!.OriginalString);

        var completed = await WaitForStatusAsync(expectedLocation, "succeeded");
        Assert.Equal(accepted.TaskId, completed.TaskId);
        Assert.Equal(approved, completed.Result.GetProperty("approved").GetBoolean());
        Assert.Equal(reason, completed.Result.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task DecisionSubmittedByPutBeforeWaitIsRetainedAndIdempotent()
    {
        var id = UniqueId();
        var submission = new ApprovalSubmission("emergency", Approved: true, "reviewed");
        var location = $"/workflows/approval/{id}/status";

        using var first = await _http.PutAsJsonAsync($"/workflows/approval/{id}", submission);
        using var duplicate = await _http.PutAsJsonAsync($"/workflows/approval/{id}", submission);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, duplicate.StatusCode);
        Assert.Equal(location, first.Headers.Location!.OriginalString);
        Assert.Equal(location, duplicate.Headers.Location!.OriginalString);

        var completed = await WaitForStatusAsync(location, "succeeded");
        Assert.True(completed.Result.GetProperty("approved").GetBoolean());
        Assert.Equal("emergency", completed.Result.GetProperty("subject").GetString());
    }

    [Fact]
    public async Task ConflictingSubjectAndDecisionCannotMutateOriginalApproval()
    {
        var id = UniqueId();
        using var start = await _http.PostAsync($"/workflows/approval/{id}?subject=production", null);
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);

        using var wrongSubject = await _http.PutAsJsonAsync(
            $"/workflows/approval/{id}",
            new ApprovalSubmission("staging", Approved: false, "wrong workflow"));
        Assert.Equal(HttpStatusCode.Conflict, wrongSubject.StatusCode);
        var afterWrongSubject = await _cluster.Client.GetGrain<IApprovalGrain>(id).GetSnapshotAsync();
        Assert.Equal("production", afterWrongSubject.Subject);
        Assert.Null(afterWrongSubject.Decision);

        var acceptedDecision = new ApprovalSubmission("production", Approved: true, "accepted");
        using var submit = await _http.PutAsJsonAsync($"/workflows/approval/{id}", acceptedDecision);
        Assert.Equal(HttpStatusCode.Accepted, submit.StatusCode);

        using var conflictingDecision = await _http.PutAsJsonAsync(
            $"/workflows/approval/{id}",
            new ApprovalSubmission("production", Approved: false, "changed"));
        Assert.Equal(HttpStatusCode.Conflict, conflictingDecision.StatusCode);

        var snapshot = await _cluster.Client.GetGrain<IApprovalGrain>(id).GetSnapshotAsync();
        Assert.Equal(new ApprovalDecision(id, Approved: true, "accepted"), snapshot.Decision);
        var completed = await WaitForStatusAsync($"/workflows/approval/{id}/status", "succeeded");
        Assert.True(completed.Result.GetProperty("approved").GetBoolean());
        Assert.Equal("production", completed.Result.GetProperty("subject").GetString());
    }

    [Fact]
    public async Task CancellationResourceIsPollableAndIdempotent()
    {
        var id = UniqueId();
        var location = $"/workflows/cancellation/{id}/status";

        using var first = await _http.PostAsync($"/workflows/cancellation/{id}", null);
        using var duplicate = await _http.PostAsync($"/workflows/cancellation/{id}", null);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, duplicate.StatusCode);
        Assert.Equal(location, first.Headers.Location!.OriginalString);
        Assert.Equal(location, duplicate.Headers.Location!.OriginalString);
        var firstAccepted = await first.Content.ReadFromJsonAsync<AcceptedDocument>();
        var duplicateAccepted = await duplicate.Content.ReadFromJsonAsync<AcceptedDocument>();
        Assert.Equal(firstAccepted, duplicateAccepted);
        Assert.Equal("pending", (await GetStatusAsync(location)).Status);

        using var cancel = await _http.DeleteAsync($"/workflows/cancellation/{id}?reason=operator-request");
        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        Assert.Equal(location, cancel.Headers.Location!.OriginalString);

        var completed = await WaitForStatusAsync(location, "succeeded");
        Assert.True(completed.Result.GetProperty("canceled").GetBoolean());
        Assert.Equal("operator-request", completed.Result.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task StatusResourcesRejectInvalidIdsAndDoNotCreateMissingWorkflows()
    {
        using var invalidApproval = await _http.PostAsync("/workflows/approval/$invalid?subject=test", null);
        using var invalidCancellation = await _http.PostAsync("/workflows/cancellation/$invalid", null);
        using var missingApproval = await _http.GetAsync($"/workflows/approval/{UniqueId()}/status");
        using var missingCancellation = await _http.GetAsync($"/workflows/cancellation/{UniqueId()}/status");

        Assert.Equal(HttpStatusCode.BadRequest, invalidApproval.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidCancellation.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingApproval.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingCancellation.StatusCode);
    }

    private async Task<StatusDocument> WaitForStatusAsync(string location, string expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var status = await GetStatusAsync(location, timeout.Token);
            if (string.Equals(status.Status, expected, StringComparison.Ordinal))
            {
                return status;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
        }
    }

    private async Task<StatusDocument> GetStatusAsync(string location, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(location, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<StatusDocument>(cancellationToken))!;
    }

    private static string UniqueId() => Guid.NewGuid().ToString("N");

    private sealed record AcceptedDocument(string TaskId, string StatusUrl);
    private sealed record StatusDocument(string TaskId, string Status, JsonElement Result, string? Error);
}
