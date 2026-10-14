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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Orleans.DurableTasks;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.TestingHost;
using Xunit;

namespace DurableWorkflows.Tests;

public sealed class WorkflowTests : IAsyncLifetime
{
    private static readonly TimeSpan ApprovalTimeout = TimeSpan.FromDays(3);
    private readonly VolatileJournalStorageProvider _storage = CreateStorage();
    private InProcessTestCluster _cluster = null!;
    private IClusterClient Client => _cluster.Client;

    public async ValueTask InitializeAsync()
    {
        _cluster = CreateCluster(1, _storage);
        await _cluster.DeployAsync();
    }

    public ValueTask DisposeAsync() => _cluster.DisposeAsync();

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
    public async Task FanOutFanInCompletesEveryBranchUnderStableChildIds()
    {
        var id = UniqueId();
        var items = new[] { "one", "two", "three" };

        var result = await RunAsync(Client.GetGrain<IWorkflowGrain>(id).RunFanOutAsync(items), $"fanout-{id}");

        Assert.Equal(items.Select(item => item.ToUpperInvariant()), result.Branches.Select(branch => branch.Output));
        // Each branch runs on its activity grain under its caller-scoped ID, which ends with the workflow's stable child ID.
        Assert.All(
            result.Branches.Select((branch, index) => (branch.TaskId, Expected: $"/fanout-{id}/branches/{index}")),
            branch => Assert.EndsWith(branch.Expected, branch.TaskId, StringComparison.Ordinal));
        for (var index = 0; index < items.Length; index++)
        {
            var snapshot = await Client.GetGrain<IActivityGrain>($"{id}-fanout-{index}").GetSnapshotAsync();
            Assert.Equal(result.Branches[index], Assert.Single(snapshot.Effects).Value);
        }
    }

    [Theory]
    [InlineData(true, "approved")]
    [InlineData(false, "rejected")]
    public async Task HumanDecisionSignalCompletesApproval(bool approved, string reason)
    {
        var id = UniqueId();
        var rootId = $"approval-{id}";
        var workflow = Client.GetGrain<IWorkflowGrain>(id);
        var scheduled = await workflow.RunApprovalAsync(new("deploy", ApprovalTimeout)).WithId(rootId).ScheduleAsync();

        await workflow.SignalDurableTaskAsync(rootId, WorkflowSignals.ApprovalDecided, new ApprovalDecision(approved, reason));
        var result = await scheduled.WaitAsync();

        Assert.Equal(id, result.WorkflowId);
        Assert.Equal("deploy", result.Subject);
        Assert.Equal(approved, result.Approved);
        Assert.Equal(reason, result.Reason);
    }

    [Fact]
    public async Task ApprovalTaskLatchesItsFirstDecision()
    {
        var id = UniqueId();
        var rootId = $"approval-{id}";
        var workflow = Client.GetGrain<IWorkflowGrain>(id);
        var decision = new ApprovalDecision(Approved: true, "recorded first");

        Assert.Equal(
            DurableSignalResult.TaskNotFound,
            await workflow.TrySignalDurableTaskAsync(rootId, WorkflowSignals.ApprovalDecided, decision));
        var scheduled = await workflow.RunApprovalAsync(new("deploy", ApprovalTimeout)).WithId(rootId).ScheduleAsync();
        Assert.Equal(
            DurableSignalResult.Accepted,
            await workflow.TrySignalDurableTaskAsync(rootId, WorkflowSignals.ApprovalDecided, decision));
        Assert.Equal(
            DurableSignalResult.Duplicate,
            await workflow.TrySignalDurableTaskAsync(rootId, WorkflowSignals.ApprovalDecided, decision));
        Assert.Equal(
            DurableSignalResult.Conflict,
            await workflow.TrySignalDurableTaskAsync(rootId, WorkflowSignals.ApprovalDecided, decision with { Approved = false }));

        var result = await scheduled.WaitAsync();
        Assert.True(result.Approved);
        Assert.Equal("recorded first", result.Reason);
    }

    [Fact]
    public async Task ApprovalRejectsAfterItsDeadline()
    {
        var id = UniqueId();
        var rootId = $"approval-{id}";
        var workflow = Client.GetGrain<IWorkflowGrain>(id);
        var scheduled = await workflow.RunApprovalAsync(new("deploy", TimeSpan.FromSeconds(1))).WithId(rootId).ScheduleAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await scheduled.WaitAsync(timeout.Token);

        Assert.False(result.Approved);
        Assert.Equal("No decision arrived before the deadline.", result.Reason);
        Assert.Equal(
            DurableSignalResult.TaskCompleted,
            await workflow.TrySignalDurableTaskAsync(rootId, WorkflowSignals.ApprovalDecided, new ApprovalDecision(true, "late")));
    }

    [Fact]
    public async Task CancellationPersistsAcrossDeactivationAndCompensates()
    {
        var id = UniqueId();
        var workflow = Client.GetGrain<IWorkflowGrain>(id);
        var inventory = Client.GetGrain<IInventoryGrain>(id);
        var scheduled = await workflow.RunCancellationAsync(TimeSpan.FromHours(1)).WithId($"cancellation-{id}").ScheduleAsync();
        await WaitUntilAsync(async () => (await inventory.GetSnapshotAsync()).Effects.ContainsKey($"{id}:reserve"));
        Assert.Equal(DurableTaskStatus.Pending, await scheduled.GetStatusAsync());

        await _cluster.DeactivateAsync(workflow);
        await scheduled.CancelAsync("operator request");
        await _cluster.DeactivateAsync(workflow);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationWorkflowResult result;
        try
        {
            result = await scheduled.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Workflow '{id}' did not complete task '{scheduled.Id}' after its cancellation was recovered from storage.",
                exception);
        }

        Assert.True(result.Canceled);
        Assert.Equal("operator request", result.Reason);
        var effects = (await inventory.GetSnapshotAsync()).Effects;
        Assert.Equal(["release-inventory", "reserve-inventory"], effects.Values.Select(effect => effect.Kind).Order());
    }

    [Fact]
    public async Task FailedSagaCompensatesSucceededStepsAndCompensationIsIdempotent()
    {
        var orderId = UniqueId();
        var workflow = Client.GetGrain<IWorkflowGrain>(orderId);
        var request = new OrderRequest(orderId, FailShipping: true);

        var rootId = $"order-{orderId}-first";
        var first = await RunAsync(workflow.RunOrderSagaAsync(request), rootId);
        var replay = await RunAsync(workflow.RunOrderSagaAsync(request), rootId);
        var duplicateWorkflow = await RunAsync(workflow.RunOrderSagaAsync(request), $"order-{orderId}-second");

        Assert.Equal(OrderWorkflowStatus.Compensated, first.Status);
        Assert.Equal(["refund-payment", "release-inventory"], first.Compensations);
        Assert.Equal($"Injected failure for {orderId}:ship.", first.Failure);
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
        var inventory = await Client.GetGrain<IInventoryGrain>(orderId).GetSnapshotAsync();
        var payment = await Client.GetGrain<IPaymentGrain>(orderId).GetSnapshotAsync();
        Assert.Equal("reserve-inventory", Assert.Single(inventory.Effects).Value.Kind);
        Assert.Equal("charge-payment", Assert.Single(payment.Effects).Value.Kind);
    }

    private static async Task<TResult> RunAsync<TResult>(DurableTask<TResult> definition, string rootId)
    {
        var scheduled = await definition.WithId(rootId).ScheduleAsync();
        return await scheduled.WaitAsync();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
        }
    }

    private static string UniqueId() => Guid.NewGuid().ToString("N");

    internal static VolatileJournalStorageProvider CreateStorage() =>
        new(Options.Create(new JournaledStateManagerOptions { JournalFormatKey = "orleans-binary" }));

    // The silos share one volatile journal store, so a grain recovers its durable tasks on any silo.
    internal static InProcessTestCluster CreateCluster(
        short silos,
        VolatileJournalStorageProvider storage,
        TimeProvider? timeProvider = null)
    {
        var builder = new InProcessTestClusterBuilder(silos);
        builder.ConfigureClient(clientBuilder => clientBuilder.AddDurableTasks());
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            siloBuilder
                .AddJournaling()
                .UseInMemoryDurableJobs()
                .Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = "orleans-binary")
                .AddDurableTasks(options => options.ResultRetentionPeriod = TimeSpan.FromHours(1))
                .AddGrainExtension<IWorkflowTestExtension, WorkflowTestExtension>();
            siloBuilder.Services.RemoveAll<IJournalStorageProvider>();
            siloBuilder.Services.RemoveAll<IJournalStorageCatalog>();
            siloBuilder.Services.AddSingleton<IJournalStorageProvider>(storage);
            siloBuilder.Services.AddSingleton<IJournalStorageCatalog>(storage);
            if (timeProvider is not null)
            {
                siloBuilder.Services.AddSingleton(timeProvider);
                siloBuilder.Services.UseTimeProviderForBackgroundAreas(TimeProvider.System);
            }
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
        var rootId = $"recovery-{id}";
        var workflow = cluster.Client.GetGrain<IWorkflowGrain>(id);
        var request = new ApprovalRequest("recover-after-failover", TimeSpan.FromDays(3));
        var scheduled = await workflow.RunApprovalAsync(request).WithId(rootId).ScheduleAsync();
        var initial = await scheduled.GetResponseAsync(new PollingOptions { PollTimeout = TimeSpan.Zero });
        Assert.True(
            initial.Status is DurableTaskStatus.Pending,
            $"Expected a pending workflow, got {initial.Status}: {initial.Exception}");

        var before = await workflow.GetRuntimeInfoAsync();
        var victim = cluster.GetActiveSilos().Single(silo =>
            string.Equals(silo.SiloAddress.ToParsableString(), before.SiloAddress, StringComparison.Ordinal));
        await cluster.KillSiloAsync(victim);
        await cluster.WaitForLivenessToStabilizeAsync(didKill: true);

        await workflow.SignalDurableTaskAsync(
            rootId,
            WorkflowSignals.ApprovalDecided,
            new ApprovalDecision(Approved: true, "completed after failover"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await scheduled.WaitAsync(timeout.Token);
        var after = await workflow.GetRuntimeInfoAsync();

        Assert.NotEqual(before.SiloAddress, after.SiloAddress);
        Assert.NotEqual(before.ActivationId, after.ActivationId);
        Assert.Equal(after.SiloAddress, result.SiloAddress);
        Assert.Equal(after.ActivationId, result.ActivationId);
        Assert.Equal(id, result.WorkflowId);
        Assert.Equal(request.Subject, result.Subject);
        Assert.True(result.Approved);
        Assert.Equal("completed after failover", result.Reason);
    }
}

public sealed class WorkflowEndpointTests : IAsyncLifetime
{
    private readonly VolatileJournalStorageProvider _storage = WorkflowTests.CreateStorage();
    private readonly FakeTimeProvider _timeProvider = new(DateTimeOffset.UtcNow);
    private InProcessTestCluster _cluster = null!;
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async ValueTask InitializeAsync()
    {
        _cluster = WorkflowTests.CreateCluster(1, _storage, _timeProvider);
        await _cluster.DeployAsync();

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IClusterClient>(_cluster.Client);
        _app = builder.Build();
        _app.MapDurableWorkflowEndpoints();
        await _app.StartAsync();

        var server = _app.Services.GetRequiredService<IServer>();
        var address = Assert.Single(server.Features.Get<IServerAddressesFeature>()!.Addresses);
        _http = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async ValueTask DisposeAsync()
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
        Assert.Equal($"approval-{id}", accepted.TaskId);

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
    public async Task DecisionSubmittedByPutBeforeStartIsRetainedAndIdempotent()
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
        var location = $"/workflows/approval/{id}/status";
        using var start = await _http.PostAsync($"/workflows/approval/{id}?subject=production", null);
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);

        using var wrongSubject = await _http.PutAsJsonAsync(
            $"/workflows/approval/{id}",
            new ApprovalSubmission("staging", Approved: false, "wrong workflow"));
        Assert.Equal(HttpStatusCode.Conflict, wrongSubject.StatusCode);
        Assert.Equal("pending", (await GetStatusAsync(location)).Status);

        using var submit = await _http.PutAsJsonAsync(
            $"/workflows/approval/{id}",
            new ApprovalSubmission("production", Approved: true, "accepted"));
        Assert.Equal(HttpStatusCode.Accepted, submit.StatusCode);

        using var conflictingDecision = await _http.PutAsJsonAsync(
            $"/workflows/approval/{id}",
            new ApprovalSubmission("production", Approved: false, "changed"));
        Assert.Equal(HttpStatusCode.Conflict, conflictingDecision.StatusCode);

        var completed = await WaitForStatusAsync(location, "succeeded");
        Assert.True(completed.Result.GetProperty("approved").GetBoolean());
        Assert.Equal("accepted", completed.Result.GetProperty("reason").GetString());
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
        using var repeated = await _http.DeleteAsync($"/workflows/cancellation/{id}?reason=second-request");
        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, repeated.StatusCode);
        Assert.Equal(location, cancel.Headers.Location!.OriginalString);

        var completed = await WaitForStatusAsync(location, "succeeded");
        Assert.True(completed.Result.GetProperty("canceled").GetBoolean());
        Assert.Equal("operator-request", completed.Result.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task StatusResourcesEscapeValidIdsAndRejectInvalidOrMissingWorkflows()
    {
        const string reservedId = "approval?fragment#";
        var encodedId = Uri.EscapeDataString(reservedId);
        using var reserved = await _http.PostAsync($"/workflows/approval/{encodedId}?subject=test", null);
        using var completeReserved = await _http.PutAsJsonAsync(
            $"/workflows/approval/{encodedId}",
            new ApprovalSubmission("test", Approved: true, "completed"));
        var nullSubjectId = UniqueId();
        using var nullSubject = await _http.PutAsJsonAsync(
            $"/workflows/approval/{nullSubjectId}",
            new { Subject = (string?)null, Approved = true, Reason = "invalid" });
        using var invalidApproval = await _http.PostAsync("/workflows/approval/$invalid?subject=test", null);
        using var invalidCancellation = await _http.PostAsync("/workflows/cancellation/$invalid", null);
        using var missingApproval = await _http.GetAsync($"/workflows/approval/{UniqueId()}/status");
        using var missingCancellation = await _http.GetAsync($"/workflows/cancellation/{UniqueId()}/status");
        using var nullSubjectStatus = await _http.GetAsync($"/workflows/approval/{nullSubjectId}/status");

        Assert.Equal(HttpStatusCode.Accepted, reserved.StatusCode);
        Assert.Equal($"/workflows/approval/{encodedId}/status", reserved.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Accepted, completeReserved.StatusCode);
        Assert.Equal("succeeded", (await WaitForStatusAsync($"/workflows/approval/{encodedId}/status", "succeeded")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, nullSubject.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, nullSubjectStatus.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidApproval.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidCancellation.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingApproval.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingCancellation.StatusCode);
    }

    [Theory]
    [InlineData("approval")]
    [InlineData("cancellation")]
    public async Task StatusLeavesUnscheduledWorkflowsMissing(string kind)
    {
        var id = UniqueId();
        var workflow = _cluster.Client.GetGrain<IWorkflowGrain>(id);
        if (kind == "approval")
        {
            // A decision for an approval which hasn't started is rejected without starting it.
            Assert.Equal(
                DurableSignalResult.TaskNotFound,
                await workflow.TrySignalDurableTaskAsync($"approval-{id}", WorkflowSignals.ApprovalDecided, new ApprovalDecision(true, "early")));
        }
        else
        {
            await workflow.GetRuntimeInfoAsync();
        }

        var location = $"/workflows/{kind}/{id}/status";
        using var first = await _http.GetAsync(location);
        using var repeated = await _http.GetAsync(location);

        Assert.Equal(HttpStatusCode.NotFound, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, repeated.StatusCode);
    }

    [Theory]
    [InlineData("approval")]
    [InlineData("cancellation")]
    public async Task ExpiredStatusRemainsMissingAcrossWorkflowReactivation(string kind)
    {
        var id = UniqueId();
        using var submit = kind == "approval"
            ? await _http.PutAsJsonAsync(
                $"/workflows/approval/{id}",
                new ApprovalSubmission("retained", Approved: true, "completed"))
            : await _http.DeleteAsync($"/workflows/cancellation/{id}?reason=completed");
        Assert.Equal(HttpStatusCode.Accepted, submit.StatusCode);
        var location = $"/workflows/{kind}/{id}/status";
        await WaitForStatusAsync(location, "succeeded");

        // The grain deletes the client-scheduled task once its retention period has elapsed, which activation checks.
        _timeProvider.Advance(TimeSpan.FromHours(2));
        var workflow = _cluster.Client.GetGrain<IWorkflowGrain>(id);
        await _cluster.DeactivateAsync(workflow);
        using var expired = await _http.GetAsync(location);
        Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);

        await _cluster.DeactivateAsync(workflow);
        using var recovered = await _http.GetAsync(location);
        Assert.Equal(HttpStatusCode.NotFound, recovered.StatusCode);
    }

    [Theory]
    [InlineData("approval")]
    [InlineData("cancellation")]
    public async Task StatusPreservesExecutionFailureAsSafeTerminalResult(string kind)
    {
        var id = UniqueId();
        var workflow = _cluster.Client.GetGrain<IWorkflowGrain>(id);
        ScheduledTask scheduled = kind == "approval"
            ? await workflow.RunApprovalAsync(new(" ", TimeSpan.FromDays(3))).WithId($"approval-{id}").ScheduleAsync()
            : await workflow.RunCancellationAsync(TimeSpan.Zero).WithId($"cancellation-{id}").ScheduleAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var failure = await scheduled.GetResponseAsync(timeout.Token);
        Assert.IsAssignableFrom<ArgumentException>(failure.Exception);

        var status = await GetStatusAsync($"/workflows/{kind}/{id}/status");
        Assert.Equal("failed", status.Status);
        Assert.Equal(scheduled.Id.ToString(), status.TaskId);
        Assert.Equal(JsonValueKind.Null, status.Result.ValueKind);
        Assert.Equal("Workflow execution failed.", status.Error);
    }

    [Theory]
    [InlineData("approval")]
    [InlineData("cancellation")]
    public async Task SameIdNotFoundFailureIsRetainedAsWorkflowFailure(string kind)
    {
        var id = UniqueId();
        var rootId = $"{kind}-{id}";
        var workflow = _cluster.Client.GetGrain<IWorkflowGrain>(id);
        var scheduled = await workflow.AsReference<IWorkflowTestExtension>()
            .FailWithNotFoundAsync()
            .WithId(rootId).ScheduleAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var failure = await scheduled.GetResponseAsync(timeout.Token);
        var missing = Assert.IsType<DurableTaskNotFoundException>(failure.Exception);
        Assert.Equal(scheduled.Id, missing.TaskId);

        await _cluster.DeactivateAsync(workflow);
        var status = await GetStatusAsync($"/workflows/{kind}/{id}/status");
        Assert.Equal("failed", status.Status);
        Assert.Equal(rootId, status.TaskId);
        Assert.Equal(JsonValueKind.Null, status.Result.ValueKind);
        Assert.Equal("Workflow execution failed.", status.Error);
    }

    private async Task<StatusDocument> WaitForStatusAsync(string location, string expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        StatusDocument? lastStatus = null;
        try
        {
            while (true)
            {
                lastStatus = await GetStatusAsync(location, timeout.Token);
                if (string.Equals(lastStatus.Status, expected, StringComparison.Ordinal))
                {
                    return lastStatus;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
            }
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Workflow status at '{location}' did not reach '{expected}'. Last response: {lastStatus}.",
                exception);
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

// Schedules a task on a workflow grain which fails with DurableTaskNotFoundException for its own ID, the exception which
// polling a missing task throws, so the status endpoint must report it as a retained failure rather than a missing workflow.
public interface IWorkflowTestExtension : IGrainExtension
{
    DurableTask<ApprovalWorkflowResult> FailWithNotFoundAsync();
}

public sealed class WorkflowTestExtension : IWorkflowTestExtension
{
    public async DurableTask<ApprovalWorkflowResult> FailWithNotFoundAsync() =>
        throw new DurableTaskNotFoundException(DurableTask.Current.Id);
}
