using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using DurableWorkflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Concurrency;
using Orleans.Durable.Jobs;
using Orleans.Durable.Journaling;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.TestingHost;
using Xunit;

namespace DurableWorkflows.Tests;

public sealed class ExternalEffectTests : IAsyncLifetime
{
    private readonly EffectClock _clock = new();
    private readonly EffectFaultStorage _storage = new();
    private readonly EffectJobGate _jobGate = new();
    private readonly ExternalEffectOptions _options = new() { AttemptTimeout = TimeSpan.FromSeconds(2) };
    private InProcessTestCluster _cluster = null!;
    private readonly string _ownerId = Guid.NewGuid().ToString("N");
    private readonly string _providerId = Guid.NewGuid().ToString("N");
    private readonly EffectIdentity _identity = new("order-run", Guid.NewGuid(), "authorize", 0);
    private IExternalEffectGrain Owner => _cluster.Client.GetGrain<IExternalEffectGrain>(_ownerId);
    private IRecipeEffectProviderGrain Provider => _cluster.Client.GetGrain<IRecipeEffectProviderGrain>(_providerId);
    private EffectRequest Payment => new(EffectKind.PaymentAuthorization, _providerId, "customer-account", "order-42", 12);
    private EffectRequest Email => new(EffectKind.Email, _providerId, "test@example.invalid", "receipt");
    private JournalId OwnerJournal => JournalId.FromGrainId(Owner.GetGrainId());

    public async ValueTask InitializeAsync()
    {
        var builder = new InProcessTestClusterBuilder(1);
        builder.ConfigureClient(client => client.AddDurableTasks());
        builder.ConfigureSilo((_, silo) =>
        {
            silo.Services.AddExternalEffectRecipes();
            silo.AddJournaling().UseInMemoryDurableJobs()
                .Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = "orleans-binary")
                .AddDurableTasks()
                .AddGrainExtension<IEffectPolicyTestExtension, EffectPolicyTestExtension>()
                .AddGrainExtension<IEffectJobTestExtension, EffectJobTestExtension>();
            var jobs = silo.Services.Last(service => service.ServiceType == typeof(ILocalDurableJobManager));
            silo.Services.AddSingleton<ILocalDurableJobManager>(services =>
                new GatedJobManager((ILocalDurableJobManager)jobs.ImplementationFactory!(services), _jobGate));
            silo.Services.RemoveAll<IJournalStorageProvider>();
            silo.Services.RemoveAll<IJournalStorageCatalog>();
            silo.Services.AddSingleton<IJournalStorageProvider>(_storage);
            silo.Services.AddSingleton<IJournalStorageCatalog>(_storage.Inner);
            silo.Services.AddKeyedSingleton<TimeProvider>(ExternalEffectOptions.ClockName, _clock);
            silo.Services.AddSingleton<IOptionsMonitor<ExternalEffectOptions>>(new EffectOptionsMonitor(_options));
        });
        _cluster = builder.Build();
        await _cluster.DeployAsync();
        Assert.Null(await Owner.GetAsync(_identity));
    }

    public async ValueTask DisposeAsync()
    {
        await Provider.ReleaseResponseAsync();
        await _cluster.DisposeAsync();
    }

    [Fact]
    public async Task FailedIntentWriteNeverDispatches()
    {
        _storage.FailNext(OwnerJournal);
        await Assert.ThrowsAnyAsync<Exception>(() => Owner.SubmitAsync(_identity, Payment));
        await _cluster.DeactivateAsync(Owner);

        Assert.Null(await Owner.GetAsync(_identity));
        await Owner.RunDueAsync();
        Assert.Empty((await Provider.GetSnapshotAsync()).DispatchKeys);
    }

    [Fact]
    public async Task CommittedIntentRecoversWithOriginalRequestPolicyAndReplayIdentity()
    {
        var accepted = await Owner.SubmitAsync(_identity, Payment);
        _options.MaxAttempts = 20;
        _options.Backoff = TimeSpan.FromHours(2);
        await _cluster.DeactivateAsync(Owner);

        var recovered = (await Owner.GetAsync(_identity))!;
        Assert.Equal(accepted.Policy, recovered.Policy);
        Assert.Equal(accepted.NextActionAt, recovered.NextActionAt);
        Assert.Equal(accepted.Request, recovered.Request);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Owner.SubmitAsync(_identity, Payment with { Amount = 24 }));
        await Owner.RunDueAsync();

        var completed = (await Owner.GetAsync(_identity))!;
        Assert.Equal(EffectStatus.Succeeded, completed.Status);
        var replay = await Owner.SubmitAsync(_identity, Payment);
        Assert.Equal(completed.ProviderKey, replay.ProviderKey);
        Assert.Equal(completed.Revision, replay.Revision);
        Assert.Single((await Provider.GetSnapshotAsync()).Applied);
        var next = await Owner.SubmitAsync(_identity with { Occurrence = 1 }, Payment);
        Assert.NotEqual(accepted.ProviderKey, next.ProviderKey);
        Assert.Equal(20, next.Policy.MaxAttempts);
        var newRun = await Owner.SubmitAsync(_identity with { Incarnation = Guid.NewGuid() }, Payment);
        Assert.NotEqual(accepted.ProviderKey, newRun.ProviderKey);
    }

    [Fact]
    public async Task LostSuccessReplyRetriesSameProviderKeyWithDistinctAttemptIdentity()
    {
        await Provider.ConfigureAsync(RecipeProviderBehavior.LoseResponseAfterApply);
        var accepted = await Owner.SubmitAsync(_identity, Payment);
        await Owner.RunDueAsync();
        var uncertain = (await Owner.GetAsync(_identity))!;
        Assert.True(uncertain.MayHaveApplied);
        Assert.Equal(EffectStatus.Reconciling, uncertain.Status);
        await _cluster.DeactivateAsync(Owner);

        _clock.Advance(accepted.Policy.Backoff);
        await Owner.RunDueAsync();

        var completed = (await Owner.GetAsync(_identity))!;
        Assert.Equal(EffectStatus.Succeeded, completed.Status);
        var remote = await Provider.GetSnapshotAsync();
        Assert.Single(remote.Applied);
        Assert.Equal(new[] { accepted.ProviderKey, accepted.ProviderKey }, remote.DispatchKeys);
        Assert.Equal(2, remote.AttemptIds.Distinct().Count());
        Assert.Equal(2, completed.Attempts.Length);
    }

    [Fact]
    public async Task MarkerCommittedWithoutDispatchRecoversAsUncertainForUnkeyedEmail()
    {
        await Owner.SubmitAsync(_identity, Email);
        _storage.FailNext(OwnerJournal, afterCommit: true);
        await Assert.ThrowsAnyAsync<Exception>(() => Owner.RunDueAsync());
        await _cluster.DeactivateAsync(Owner);
        var interrupted = (await Owner.GetAsync(_identity))!;
        Assert.Equal(EffectStatus.Attempting, interrupted.Status);
        Assert.True(interrupted.MayHaveApplied);
        Assert.Single(interrupted.Attempts);
        Assert.Empty((await Provider.GetSnapshotAsync()).Applied);

        _clock.Advance(interrupted.Policy.AttemptTimeout + interrupted.Policy.Backoff);
        await Owner.RunDueAsync();

        var reconciled = (await Owner.GetAsync(_identity))!;
        Assert.Equal(EffectStatus.Reconciling, reconciled.Status);
        Assert.Contains("operator", reconciled.NextAction, StringComparison.Ordinal);
        Assert.NotEmpty(reconciled.ResolutionOwner);
        Assert.Empty((await Provider.GetSnapshotAsync()).DispatchKeys);
    }

    [Fact]
    public async Task ProviderSuccessSurvivesFailedCompletionWriteAndOwnerRecovery()
    {
        await Provider.HoldNextResponseAsync();
        var accepted = await Owner.SubmitAsync(_identity, Payment);
        var attempt = Owner.RunDueAsync();
        await WorkflowTests.WaitUntilAsync(async () => (await Provider.GetSnapshotAsync()).Applied.ContainsKey(accepted.ProviderKey));
        _storage.FailNext(OwnerJournal);
        await Provider.ReleaseResponseAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => attempt);
        await _cluster.DeactivateAsync(Owner);

        var recovered = (await Owner.GetAsync(_identity))!;
        Assert.Equal(EffectStatus.Attempting, recovered.Status);
        _clock.Advance(recovered.Policy.AttemptTimeout + recovered.Policy.Backoff);
        await Owner.RunDueAsync();

        Assert.Equal(EffectStatus.Succeeded, (await Owner.GetAsync(_identity))!.Status);
        Assert.Single((await Provider.GetSnapshotAsync()).Applied);
    }

    [Fact]
    public async Task SafetyExpiryUsesLookupAndQueryFailurePreservesUncertaintyAndNextAction()
    {
        await Provider.ConfigureAsync(RecipeProviderBehavior.LoseResponseAfterApply);
        var accepted = await Owner.SubmitAsync(_identity, Payment);
        await Owner.RunDueAsync();
        await Provider.ConfigureAsync(RecipeProviderBehavior.LookupUnavailable);
        _clock.Advance(TimeSpan.FromDays(2));

        await Owner.RunDueAsync();
        var unresolved = (await Owner.GetAsync(_identity))!;
        Assert.Equal(EffectStatus.Reconciling, unresolved.Status);
        Assert.True(unresolved.MayHaveApplied);
        Assert.Equal(accepted.Request, unresolved.Request);
        Assert.Equal(accepted.ProviderKey, unresolved.ProviderKey);
        Assert.Contains("Lookup failed", unresolved.Evidence, StringComparison.Ordinal);
        Assert.True(unresolved.NextActionAt > _clock.GetUtcNow());
        Assert.Single((await Provider.GetSnapshotAsync()).DispatchKeys);

        await Provider.ConfigureAsync(RecipeProviderBehavior.Apply);
        _clock.Advance(unresolved.Policy.RecheckInterval);
        await Owner.RunDueAsync();
        Assert.Equal(EffectStatus.Succeeded, (await Owner.GetAsync(_identity))!.Status);
        Assert.Single((await Provider.GetSnapshotAsync()).DispatchKeys);
    }

    [Theory]
    [InlineData(RecipeProviderBehavior.ProveNoEffect, EffectStatus.Failed)]
    [InlineData(RecipeProviderBehavior.LoseResponseAfterApply, EffectStatus.Succeeded)]
    public async Task AttemptExhaustionDistinguishesConfirmedFailureFromUnknownOutcome(RecipeProviderBehavior behavior, EffectStatus expected)
    {
        _options.MaxAttempts = 1;
        await Provider.ConfigureAsync(behavior);
        var accepted = await Owner.SubmitAsync(_identity, Payment);
        await Owner.RunDueAsync();
        await _cluster.DeactivateAsync(Owner);
        _clock.Advance(accepted.Policy.Backoff);
        await Owner.RunDueAsync();

        var final = (await Owner.GetAsync(_identity))!;
        Assert.Equal(expected, final.Status);
        Assert.Single(final.Attempts);
        Assert.Single((await Provider.GetSnapshotAsync()).DispatchKeys);
    }

    [Fact]
    public async Task CancellationBeforeDispatchPreventsProviderCall()
    {
        await Owner.SubmitAsync(_identity, Payment);
        await Owner.CancelAsync(_identity, "order canceled");
        await _cluster.DeactivateAsync(Owner);
        await Owner.RunDueAsync();

        Assert.Equal(EffectStatus.Canceled, (await Owner.GetAsync(_identity))!.Status);
        Assert.Empty((await Provider.GetSnapshotAsync()).DispatchKeys);
    }

    [Fact]
    public async Task CancellationAfterRemoteSuccessRetainsAppliedOutcomeAndFencesLateResponse()
    {
        await Provider.HoldNextResponseAsync();
        var accepted = await Owner.SubmitAsync(_identity, Payment);
        var attempt = Owner.RunDueAsync();
        await WorkflowTests.WaitUntilAsync(async () => (await Provider.GetSnapshotAsync()).Applied.ContainsKey(accepted.ProviderKey));

        var canceled = await Owner.CancelAsync(_identity, "operator canceled");
        Assert.Equal(EffectStatus.Reconciling, canceled.Status);
        await Provider.ReleaseResponseAsync();
        await attempt;
        Assert.Equal(canceled.Revision, (await Owner.GetAsync(_identity))!.Revision);
        await Owner.RunDueAsync();

        var final = (await Owner.GetAsync(_identity))!;
        Assert.Equal(EffectStatus.Succeeded, final.Status);
        Assert.True(final.CancellationRequested);
        Assert.Equal("operator canceled", final.CancellationReason);
        Assert.Single((await Provider.GetSnapshotAsync()).DispatchKeys);
    }

    [Fact]
    public async Task OperatorDecisionFencesStaleResponseAndFollowUpHasNewKeyAndAuthorization()
    {
        await Provider.HoldNextResponseAsync();
        var accepted = await Owner.SubmitAsync(_identity, Email);
        var attempt = Owner.RunDueAsync();
        await WorkflowTests.WaitUntilAsync(async () => (await Provider.GetSnapshotAsync()).Applied.ContainsKey(accepted.ProviderKey));
        var resolution = await Owner.ResolveAsync(_identity, 1, ProviderOutcome.Applied, "receipt verified by operator");
        await Provider.ReleaseResponseAsync();
        await attempt;

        var final = (await Owner.GetAsync(_identity))!;
        Assert.Equal(resolution.Revision, final.Revision);
        Assert.Equal("operator: receipt verified by operator", final.Evidence);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Owner.ResolveAsync(_identity, 1, ProviderOutcome.Rejected, "stale decision"));

        var nextIdentity = _identity with { OperationId = "authorized-resend" };
        var next = await Owner.AuthorizeFollowUpAsync(_identity, nextIdentity, "support ticket 42: customer requested another copy");
        Assert.NotEqual(final.ProviderKey, next.ProviderKey);
        Assert.Equal(final.ProviderKey, next.RelatedEffectKey);
        Assert.NotEmpty(next.Authorization!);
        Assert.Equal(next.ProviderKey, (await Owner.GetAsync(_identity))!.FollowUpEffectKey);
        Assert.Equal(next.ProviderKey, (await Owner.AuthorizeFollowUpAsync(_identity, nextIdentity, next.Authorization!)).ProviderKey);
    }

    [Fact]
    public async Task PaymentOutcomeBusinessStateAndNextEmailIntentRecoverTogether()
    {
        var request = Payment with { OnSuccess = Email with { Kind = EffectKind.IdempotentEmail } };
        var accepted = await Owner.SubmitAsync(_identity, request);
        await Owner.RunDueAsync();
        await _cluster.DeactivateAsync(Owner);

        var result = (await Owner.GetAsync(_identity))!;
        var next = (await Owner.GetAsync(_identity with { OperationId = "authorize/on-success" }))!;
        Assert.Equal(EffectStatus.Succeeded, result.Status);
        Assert.Equal("authorized;ready-to-fulfill", result.BusinessState);
        Assert.Equal(next.ProviderKey, result.NextEffectKey);
        Assert.Equal(accepted.ProviderKey, next.RelatedEffectKey);
        Assert.Equal(request.OnSuccess, next.Request);
        Assert.Equal(EffectStatus.Pending, next.Status);
        var order = _cluster.Client.GetGrain<IEffectOrderGrain>(_ownerId);
        await WorkflowTests.WaitUntilAsync(async () => (await order.GetOutcomesAsync()).ContainsKey(result.ProviderKey));
        var delivered = (await order.GetOutcomesAsync())[result.ProviderKey];
        Assert.Equal(result.BusinessState, delivered.BusinessState);
        Assert.Equal(next.ProviderKey, delivered.NextEffectKey);
    }

    [Fact]
    public async Task DeclineIsDurableBusinessOutcomeAndNeverCreatesSuccessIntent()
    {
        await Provider.ConfigureAsync(RecipeProviderBehavior.Reject);
        await Owner.SubmitAsync(_identity, Payment with { OnSuccess = Email });
        await Owner.RunDueAsync();
        await _cluster.DeactivateAsync(Owner);

        var declined = (await Owner.GetAsync(_identity))!;
        Assert.Equal(EffectStatus.Rejected, declined.Status);
        Assert.Equal("business-rejected", declined.BusinessState);
        Assert.Null(declined.NextEffectKey);
        Assert.Null(await Owner.GetAsync(_identity with { OperationId = "authorize/on-success" }));
        Assert.Empty((await Provider.GetSnapshotAsync()).Applied);
    }

    [Fact]
    public async Task PolicyChangeIsExplicitValidatedAndPreservesConsumedAttempts()
    {
        var initial = await Owner.SubmitAsync(_identity, Payment);
        await Assert.ThrowsAsync<ArgumentException>(() => Owner.ChangePolicyAsync(_identity, initial.Revision,
            initial.Policy with { Version = 99 }, "upgrade"));
        var changed = await Owner.ChangePolicyAsync(_identity, initial.Revision, initial.Policy with { MaxAttempts = 2 }, "approved retry budget");
        Assert.Equal(2, changed.Policy.MaxAttempts);
        await Provider.ConfigureAsync(RecipeProviderBehavior.ProveNoEffect);
        await Owner.RunDueAsync();
        var attempted = (await Owner.GetAsync(_identity))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Owner.ChangePolicyAsync(_identity, attempted.Revision,
            attempted.Policy with { MaxAttempts = 30 }, "reset budget"));
        await _cluster.DeactivateAsync(Owner);
        var recovered = (await Owner.GetAsync(_identity))!;
        Assert.Single(recovered.Attempts);
        Assert.Equal(attempted.NextActionAt, recovered.NextActionAt);
        Assert.Equal(2, recovered.Policy.MaxAttempts);
    }

    [Fact]
    public async Task LaterNoEffectAttemptPreservesEarlierUnknownOutcomeAfterRetryExhaustion()
    {
        _options.MaxAttempts = 2;
        await Provider.ConfigureAsync(RecipeProviderBehavior.UnavailableBeforeApply);
        var initial = await Owner.SubmitAsync(_identity, Payment);
        await Owner.RunDueAsync();
        await Provider.ConfigureAsync(RecipeProviderBehavior.ProveNoEffect);
        _clock.Advance(initial.Policy.Backoff);
        await Owner.RunDueAsync();
        var attempted = (await Owner.GetAsync(_identity))!;
        Assert.True(attempted.MayHaveApplied);
        Assert.Equal(ProviderOutcome.Unknown, attempted.Attempts[0].Outcome);
        Assert.Equal(ProviderOutcome.ProvenNoEffect, attempted.Attempts[1].Outcome);

        _clock.Advance(initial.Policy.Backoff);
        await Owner.RunDueAsync();
        var exhausted = (await Owner.GetAsync(_identity))!;
        Assert.Equal(EffectStatus.Reconciling, exhausted.Status);
        Assert.True(exhausted.MayHaveApplied);
        Assert.Equal(2, (await Provider.GetSnapshotAsync()).DispatchKeys.Length);
        Assert.False(exhausted.IsCompleted);
    }

    [Fact]
    public async Task UnsupportedStoredPolicyBlocksDispatchUntilExplicitValidatedChange()
    {
        var accepted = await Owner.SubmitAsync(_identity, Payment);
        await Owner.AsReference<IEffectPolicyTestExtension>().StorePolicyAsync(_identity, accepted.Policy with { Version = 99 });
        await _cluster.DeactivateAsync(Owner);
        await Owner.RunDueAsync();
        var blocked = (await Owner.GetAsync(_identity))!;
        Assert.Equal(EffectStatus.Blocked, blocked.Status);
        Assert.Equal(99, blocked.Policy.Version);
        Assert.Contains("operator", blocked.NextAction, StringComparison.Ordinal);
        Assert.True(blocked.NextActionAt > _clock.GetUtcNow());
        Assert.Empty((await Provider.GetSnapshotAsync()).DispatchKeys);

        await Owner.ChangePolicyAsync(_identity, blocked.Revision, accepted.Policy, "operator approved supported version");
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Owner.RunDueAsync();
        Assert.Equal(EffectStatus.Succeeded, (await Owner.GetAsync(_identity))!.Status);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public async Task DispatchTimeoutMustFitStrictlyInsideSafetyWindow(int ticksPastBoundary, bool dispatched)
    {
        var accepted = await Owner.SubmitAsync(_identity, Payment);
        var safety = _clock.GetUtcNow() + TimeSpan.FromMinutes(1);
        await Owner.ChangePolicyAsync(_identity, accepted.Revision, accepted.Policy with { IdempotencySafetyDeadline = safety }, "provider retention contract");
        _clock.Advance(safety - _clock.GetUtcNow() - accepted.Policy.AttemptTimeout + TimeSpan.FromTicks(ticksPastBoundary));
        await Owner.RunDueAsync();

        Assert.Equal(dispatched ? EffectStatus.Succeeded : EffectStatus.Failed, (await Owner.GetAsync(_identity))!.Status);
        Assert.Equal(dispatched ? 1 : 0, (await Provider.GetSnapshotAsync()).DispatchKeys.Length);
    }

    [Fact]
    public async Task LostCompletionAcknowledgementRecoversOutcomeAndNextIntentInOneWrite()
    {
        await Provider.HoldNextResponseAsync();
        var accepted = await Owner.SubmitAsync(_identity, Payment with { OnSuccess = Email });
        var attempt = Owner.RunDueAsync();
        await WorkflowTests.WaitUntilAsync(async () => (await Provider.GetSnapshotAsync()).Applied.ContainsKey(accepted.ProviderKey));
        _storage.FailNext(OwnerJournal, afterCommit: true);
        await Provider.ReleaseResponseAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => attempt);
        await _cluster.DeactivateAsync(Owner);

        var completed = (await Owner.GetAsync(_identity))!;
        var outgoing = (await Owner.GetAsync(_identity with { OperationId = "authorize/on-success" }))!;
        Assert.Equal(EffectStatus.Succeeded, completed.Status);
        Assert.Equal("authorized;ready-to-fulfill", completed.BusinessState);
        Assert.Equal(outgoing.ProviderKey, completed.NextEffectKey);
        Assert.Equal(accepted.ProviderKey, outgoing.RelatedEffectKey);
        Assert.Single((await Provider.GetSnapshotAsync()).DispatchKeys);
    }

    [Fact]
    public async Task SlowMarkerAcknowledgementRechecksSafetyDeadlineBeforeDispatch()
    {
        var accepted = await Owner.SubmitAsync(_identity, Payment);
        await Owner.ChangePolicyAsync(_identity, accepted.Revision, accepted.Policy with
        {
            IdempotencySafetyDeadline = _clock.GetUtcNow() + TimeSpan.FromMinutes(1)
        }, "provider retention contract");
        var gate = _storage.HoldNextAcknowledgement(OwnerJournal);
        var attempt = Owner.RunDueAsync();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromMinutes(1));
        gate.Release.SetResult();
        await attempt;

        var result = (await Owner.GetAsync(_identity))!;
        Assert.Equal(EffectStatus.Failed, result.Status);
        Assert.False(result.MayHaveApplied);
        Assert.Equal(ProviderOutcome.ProvenNoEffect, Assert.Single(result.Attempts).Outcome);
        Assert.Empty((await Provider.GetSnapshotAsync()).DispatchKeys);
    }

    [Fact]
    public async Task RefundIsASeparatelyAuthorizedEffectAndPreservesOriginalReceiptIntent()
    {
        var accepted = await Owner.SubmitAsync(_identity, Payment with { OnSuccess = Email });
        await Owner.RunDueAsync();
        var original = (await Owner.GetAsync(_identity))!;
        var refund = await Owner.AuthorizeFollowUpAsync(_identity, _identity with { OperationId = "refund" },
            "return approved", Payment with { Kind = EffectKind.PaymentRefund });
        Assert.Equal(EffectKind.PaymentRefund, refund.Request.Kind);
        Assert.Equal(accepted.ProviderKey, refund.RelatedEffectKey);
        await Owner.RunDueAsync();

        var settled = (await Owner.GetAsync(refund.Identity))!;
        Assert.Equal(EffectStatus.Succeeded, settled.Status);
        Assert.Equal("refunded", settled.BusinessState);
        var payment = (await Owner.GetAsync(_identity))!;
        Assert.Equal(EffectStatus.Succeeded, payment.Status);
        Assert.Equal(original.NextEffectKey, payment.NextEffectKey);
        Assert.Equal(refund.ProviderKey, payment.FollowUpEffectKey);
    }

    [Fact]
    public async Task FollowUpAuthorizationRejectsAttemptWhichStartsWhileSchedulingItsWakeup()
    {
        var accepted = await Owner.SubmitAsync(_identity, Email);
        var followUp = _identity with { OperationId = "authorized-resend" };
        var authorizationWakeup = _jobGate.HoldNext();
        var authorization = Owner.AuthorizeFollowUpAsync(_identity, followUp, "support ticket 43");
        await authorizationWakeup.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // A due job interleaves with the authorization and holds its attempt before recording a newer revision.
        var attemptWakeup = _jobGate.HoldNext();
        var attempt = Owner.AsReference<IEffectJobTestExtension>().RunDueJobAsync();
        await attemptWakeup.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        authorizationWakeup.Release.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => authorization);
        attemptWakeup.Release.SetResult();
        await attempt;

        var original = (await Owner.GetAsync(_identity))!;
        Assert.Null(original.FollowUpEffectKey);
        Assert.False(original.CancellationRequested);
        Assert.Equal(EffectStatus.Succeeded, original.Status);
        Assert.Null(await Owner.GetAsync(followUp));
        Assert.Equal([accepted.ProviderKey], (await Provider.GetSnapshotAsync()).DispatchKeys);
    }

    [Fact]
    public void WakeupInfrastructureRetriesRemainEligibleAfterProviderAttemptBudget()
    {
        var services = _cluster.GetSiloServiceProvider();
        var jobs = services.GetRequiredService<IOptions<DurableJobsOptions>>().Value;
        var job = new DurableJob
        {
            Id = "wakeup",
            Name = ExternalEffectHosting.WakeupJobName,
            DueTime = DateTimeOffset.UtcNow,
            TargetGrainId = Owner.GetGrainId(),
            ShardId = "shard"
        };
        var next = jobs.ShouldRetry(new EffectJobContext(job, 100), new IOException("storage unavailable"));
        Assert.NotNull(next);
        Assert.True(next > DateTimeOffset.UtcNow);
    }
}

internal sealed record EffectJobContext(DurableJob Job, int DequeueCount) : IJobRunContext
{
    public string RunId => "infrastructure-attempt";
}

public interface IEffectPolicyTestExtension : IGrainExtension
{
    Task StorePolicyAsync(EffectIdentity identity, EffectPolicy policy);
}

public sealed class EffectPolicyTestExtension(IDurableStateManager manager, IGrainContext context) : IEffectPolicyTestExtension
{
    public async Task StorePolicyAsync(EffectIdentity identity, EffectPolicy policy)
    {
        var effects = manager.GetOrAddDictionary<string, EffectRecord>("external-effects");
        var key = identity.ProviderKey(context.GrainId.ToString());
        effects[key] = effects[key] with { Policy = policy };
        await manager.WriteStateAsync();
    }
}

// Durable job delivery interleaves with the grain's other calls; this extension runs the handler the same way.
public interface IEffectJobTestExtension : IGrainExtension
{
    [AlwaysInterleave]
    Task RunDueJobAsync();
}

public sealed class EffectJobTestExtension(IGrainContext context) : IEffectJobTestExtension
{
    public Task RunDueJobAsync()
    {
        var job = new DurableJob
        {
            Id = "wakeup",
            Name = ExternalEffectHosting.WakeupJobName,
            DueTime = DateTimeOffset.UtcNow,
            TargetGrainId = context.GrainId,
            ShardId = "shard"
        };
        return ((IDurableJobHandler)context.GrainInstance!).ExecuteJobAsync(new EffectJobContext(job, 1), CancellationToken.None);
    }
}

// Holds the next queued wake-up scheduling calls until a test releases them.
internal sealed class EffectJobGate
{
    private readonly ConcurrentQueue<EffectWriteGate> _holds = new();

    public EffectWriteGate HoldNext()
    {
        var gate = new EffectWriteGate();
        _holds.Enqueue(gate);
        return gate;
    }

    public bool TryTakeHold([NotNullWhen(true)] out EffectWriteGate? gate) => _holds.TryDequeue(out gate);
}

internal sealed class GatedJobManager(ILocalDurableJobManager inner, EffectJobGate gate) : ILocalDurableJobManager
{
    public async Task<DurableJob> ScheduleJobAsync(ScheduleJobRequest request, CancellationToken cancellationToken)
    {
        if (gate.TryTakeHold(out var hold))
        {
            hold.Entered.SetResult();
            await hold.Release.Task.WaitAsync(cancellationToken);
        }

        return await inner.ScheduleJobAsync(request, cancellationToken);
    }

    public Task<bool> CancelAsync(DurableJob job, CancellationToken requestCancellationToken) =>
        inner.CancelAsync(job, requestCancellationToken);
}

internal sealed class EffectClock : TimeProvider
{
    // Keep scheduled jobs in the real scheduler's future; tests advance the owner clock and invoke due work explicitly.
    private long _ticks = DateTimeOffset.UtcNow.AddDays(1).Ticks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    public void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
}

internal sealed class EffectOptionsMonitor(ExternalEffectOptions options) : IOptionsMonitor<ExternalEffectOptions>
{
    public ExternalEffectOptions CurrentValue => options;
    public ExternalEffectOptions Get(string? name) => options;
    public IDisposable? OnChange(Action<ExternalEffectOptions, string?> listener) => null;
}

internal sealed class EffectFaultStorage : IJournalStorageProvider, IJournalStorageCatalog
{
    public VolatileJournalStorageProvider Inner { get; } = WorkflowTests.CreateStorage();
    private readonly ConcurrentDictionary<string, bool> _failures = new();
    private readonly ConcurrentDictionary<string, EffectWriteGate> _holds = new();

    public void FailNext(JournalId journal, bool afterCommit = false) => _failures[journal.Value] = afterCommit;
    public EffectWriteGate HoldNextAcknowledgement(JournalId journal)
    {
        var gate = new EffectWriteGate();
        _holds[journal.Value] = gate;
        return gate;
    }
    public IJournalStorage CreateStorage(JournalId journalId) => new FaultStorage(this, journalId, Inner.CreateStorage(journalId));
    public IAsyncEnumerable<JournalCatalogEntry> ListAsync(JournalCatalogListOptions? options = null, CancellationToken cancellationToken = default) =>
        Inner.ListAsync(options, cancellationToken);

    private sealed class FaultStorage(EffectFaultStorage owner, JournalId id, IJournalStorage inner) : IJournalStorage
    {
        public bool IsCompactionRequested => inner.IsCompactionRequested;
        public ValueTask<bool> CreateIfNotExistsAsync(IReadOnlyDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default) =>
            inner.CreateIfNotExistsAsync(metadata, cancellationToken);
        public ValueTask<IJournalMetadata?> GetMetadataAsync(CancellationToken cancellationToken = default) => inner.GetMetadataAsync(cancellationToken);
        public ValueTask<IJournalMetadata?> UpdateMetadataAsync(IReadOnlyDictionary<string, string>? set = null, IEnumerable<string>? remove = null,
            string? expectedETag = null, CancellationToken cancellationToken = default) => inner.UpdateMetadataAsync(set, remove, expectedETag, cancellationToken);
        public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken) => inner.ReadAsync(consumer, cancellationToken);
        public ValueTask DeleteAsync(CancellationToken cancellationToken) => inner.DeleteAsync(cancellationToken);
        public ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) => WriteAsync(value, false, cancellationToken);
        public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) => WriteAsync(value, true, cancellationToken);

        private async ValueTask WriteAsync(ReadOnlySequence<byte> value, bool replace, CancellationToken cancellationToken)
        {
            var fail = owner._failures.TryRemove(id.Value, out var afterCommit);
            if (fail && !afterCommit)
            {
                throw new IOException("Injected journal write rejection.");
            }

            if (replace)
            {
                await inner.ReplaceAsync(value, cancellationToken);
            }
            else
            {
                await inner.AppendAsync(value, cancellationToken);
            }

            if (owner._holds.TryRemove(id.Value, out var gate))
            {
                gate.Entered.SetResult();
                await gate.Release.Task.WaitAsync(cancellationToken);
            }

            if (fail)
            {
                throw new IOException("Injected acknowledgement loss after journal publication.");
            }

        }
    }
}

internal sealed class EffectWriteGate
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
