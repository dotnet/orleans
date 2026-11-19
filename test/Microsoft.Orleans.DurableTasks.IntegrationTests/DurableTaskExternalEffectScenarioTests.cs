using System.Text.Json.Serialization;
using DurableWorkflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Orleans.DurableTasks.IntegrationTests.FaultInjection;
using Orleans.Journaling;
using Orleans.Journaling.Json;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests;

/// <summary>Runs the sample's actual payment/email owner and separately journaled provider against controlled boundaries.</summary>
public abstract class DurableTaskExternalEffectScenarioTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly DurableTaskRecoveryFixture _fixture;
    private readonly AdvanceableTimeProvider _clock = new();
    private readonly ScenarioJournalGate _gate = new();
    private readonly DurableTaskFaultInjector _faults = new();
    private readonly string _format;
    private string _lastBarrier = "initialization";
    private string _schedule = "initial external-effect-next-action armed by submission";

    protected DurableTaskExternalEffectScenarioTests(string format)
    {
        _format = format;
        // Keep automatic scheduler delivery in the future. These cases invoke the production due-job handler
        // explicitly; the application owner, not membership or Durable Jobs, owns this clock.
        _clock.Advance(TimeSpan.FromDays(2));
        _fixture = new(format, silo =>
        {
            silo.Services.AddExternalEffectRecipes();
            silo.Services.AddKeyedSingleton<TimeProvider>(ExternalEffectOptions.ClockName, _clock);
            silo.Services.Configure<ExternalEffectOptions>(options =>
            {
                options.MaxAttempts = 2;
                options.Backoff = TimeSpan.FromHours(1);
                options.RecheckInterval = TimeSpan.FromHours(1);
                options.Lifetime = TimeSpan.FromDays(1);
                options.ProviderKeyLifetime = TimeSpan.FromDays(2);
            });
            silo.Services.Configure<JsonJournalOptions>(options =>
                options.AddTypeInfoResolver(ExternalEffectScenarioJsonContext.Default));
            _gate.Configure(silo);
            _faults.Configure(silo);
        });
    }

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();
    public async ValueTask DisposeAsync()
    {
        _gate.Release();
        _faults.Reset();
        await _fixture.DisposeAsync();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EffectIntentAcknowledgementFollowsStorageNotStaging(bool afterAcknowledgement)
    {
        var (owner, _, identity, request) = await CreateAsync(EffectKind.PaymentAuthorization, RecipeProviderBehavior.Apply);
        // Establish the initial snapshot so the selected write is an append, not initial journal replacement.
        var existingIdentity = identity with { OperationId = "existing-intent" };
        await owner.SubmitAsync(existingIdentity, request);
        var key = identity.ProviderKey(owner.GetGrainId().ToString());
        _gate.Arm(owner.GetGrainId(), afterAcknowledgement);
        var submitting = owner.SubmitAsync(identity, request);
        try
        {
            await WaitAsync(_gate.Entered, owner, afterAcknowledgement ? "storage acknowledged; continuation held" : "before append admission");
            Assert.False(_gate.IsReplacement);
            var committed = await ReadDictionaryAsync<EffectRecord>(owner.GetGrainId(), "external-effects");
            if (afterAcknowledgement)
            {
                Assert.Equal(2, committed.Count);
                var stored = committed[key];
                Assert.Equal(identity, stored.Identity);
                Assert.Equal(request, stored.Request);
                Assert.Equal(EffectStatus.Pending, stored.Status);
            }
            else
            {
                Assert.Equal(existingIdentity, Assert.Single(committed).Value.Identity);
                Assert.False(committed.ContainsKey(key));
            }

            Assert.False(submitting.IsCompleted);
        }
        finally
        {
            _gate.Release();
        }

        await AssertPersistedAsync(owner, await WaitAsync(submitting, owner, "intent acknowledgement"));
    }

    [Fact]
    public async Task PaymentAppliedWithLostResponseRetriesTheSameKeyAfterOwnerRecovery()
    {
        // Contract: a payment whose response is lost reuses its provider key, with a fresh attempt ID and one applied effect.
        var (owner, provider, identity, request) = await CreateAsync(
            EffectKind.PaymentAuthorization, RecipeProviderBehavior.LoseResponseAfterApply);
        var accepted = await owner.SubmitAsync(identity, request);
        await WaitAsync(owner.RunDueAsync(), owner, "provider applied payment and lost response");
        var uncertain = Assert.IsType<EffectRecord>(await owner.GetAsync(identity));
        Assert.Equal(EffectStatus.Reconciling, uncertain.Status);
        Assert.True(uncertain.MayHaveApplied);
        Assert.Single(uncertain.Attempts);
        var first = await provider.GetSnapshotAsync();
        Assert.Equal(request, Assert.Single(first.Applied).Value);
        Assert.Equal([accepted.ProviderKey], first.DispatchKeys);
        await AssertPersistedAsync(owner, uncertain);

        await _fixture.Cluster.DeactivateAsync(owner);
        await WaitAsync(owner.RunDueAsync(), owner, "retry not yet due after recovery");
        Assert.Equal(first.DispatchKeys, (await provider.GetSnapshotAsync()).DispatchKeys);
        AdvanceTo(uncertain.NextActionAt);
        await WaitAsync(owner.RunDueAsync(), owner, "same-key payment retry completed");

        var completed = Assert.IsType<EffectRecord>(await owner.GetAsync(identity));
        var observed = await provider.GetSnapshotAsync();
        Assert.Equal(EffectStatus.Succeeded, completed.Status);
        Assert.Equal("authorized;ready-to-fulfill", completed.BusinessState);
        Assert.Equal([accepted.ProviderKey, accepted.ProviderKey], observed.DispatchKeys);
        Assert.Equal(2, observed.AttemptIds.Distinct().Count());
        Assert.Equal(request, Assert.Single(observed.Applied).Value);
        await AssertPersistedAsync(owner, completed);
    }

    [Fact]
    public async Task InterruptedNonRepeatableEmailRecoversItsReconciliationOwnerWithoutRedispatch()
    {
        // Contract: after a non-repeatable provider applies, loss of the owner's result write recovers the committed
        // attempt marker. Recovery must reconcile, never interpret the missing answer as permission to resend.
        var (owner, provider, identity, request) = await CreateAsync(EffectKind.Email, RecipeProviderBehavior.Apply);
        await provider.HoldNextResponseAsync();
        _gate.Arm(provider.GetGrainId(), afterAcknowledgement: true);
        var accepted = await owner.SubmitAsync(identity, request);
        var running = owner.RunDueAsync();
        try
        {
            await WaitAsync(_gate.Entered, owner, "provider applied email; storage acknowledged before response");
            var applied = await ReadDictionaryAsync<EffectRequest>(provider.GetGrainId(), "applied");
            Assert.Equal(request, Assert.Single(applied).Value);
            var failure = _faults.Arm(FaultPoint.JournalWriteBeforeDurable, owner.GetGrainId(), 0);
            _gate.Release();
            await provider.ReleaseResponseAsync();
            await Assert.ThrowsAsync<InjectedFaultException>(() => WaitAsync(running, owner, "owner result write failed"));
            Assert.True(failure.HasFired);
        }
        finally
        {
            _gate.Release();
            await provider.ReleaseResponseAsync();
        }

        var recovered = Assert.IsType<EffectRecord>(await owner.GetAsync(identity));
        Assert.Equal(EffectStatus.Attempting, recovered.Status);
        Assert.True(recovered.MayHaveApplied);
        Assert.Equal($"effect-operator/{owner.GetPrimaryKeyString()}", recovered.ResolutionOwner);
        Assert.Single(recovered.Attempts);
        AdvanceTo(recovered.NextActionAt);
        await WaitAsync(owner.RunDueAsync(), owner, "interrupted email reconciled without authoritative lookup");

        var reconciling = Assert.IsType<EffectRecord>(await owner.GetAsync(identity));
        Assert.Equal(EffectStatus.Reconciling, reconciling.Status);
        Assert.False(reconciling.IsCompleted);
        Assert.Equal(accepted.ResolutionOwner, reconciling.ResolutionOwner);
        Assert.Equal("provider-lookup-or-operator-decision", reconciling.NextAction);
        Assert.True(reconciling.NextActionAt > _clock.GetUtcNow());
        Assert.Equal([accepted.ProviderKey], (await provider.GetSnapshotAsync()).DispatchKeys);
        await AssertPersistedAsync(owner, reconciling);
    }

    [Fact]
    public async Task ExhaustedRetryDecisionAndAuthorizedFollowUpSurviveImmediateSiloLoss()
    {
        // Contract: exhausting proven-no-effect attempts yields a durable terminal decision. A separately authorized
        // follow-up retains its own identity and linkage across immediate silo loss without reopening the original.
        var (owner, provider, identity, request) = await CreateAsync(
            EffectKind.PaymentAuthorization, RecipeProviderBehavior.ProveNoEffect);
        var accepted = await owner.SubmitAsync(identity, request);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await WaitAsync(owner.RunDueAsync(), owner, $"proven-no-effect attempt {attempt + 1}");
            var pending = Assert.IsType<EffectRecord>(await owner.GetAsync(identity));
            Assert.Equal(attempt + 1, pending.Attempts.Length);
            Assert.False(pending.MayHaveApplied);
            AdvanceTo(pending.NextActionAt);
        }

        await WaitAsync(owner.RunDueAsync(), owner, "retry budget exhausted");
        var failed = Assert.IsType<EffectRecord>(await owner.GetAsync(identity));
        Assert.Equal(EffectStatus.Failed, failed.Status);
        Assert.Equal("confirmed-no-effect", failed.BusinessState);
        Assert.True(failed.IsCompleted);
        Assert.Equal([accepted.ProviderKey, accepted.ProviderKey], (await provider.GetSnapshotAsync()).DispatchKeys);
        var followUpIdentity = identity with { Occurrence = 1 };
        var followUp = await owner.AuthorizeFollowUpAsync(identity, followUpIdentity, "operator-approved retry after confirmed no effect");
        await AssertPersistedAsync(owner, Assert.IsType<EffectRecord>(await owner.GetAsync(identity)));
        await AssertPersistedAsync(owner, followUp);
        Assert.True(_fixture.Cluster.TryGetGrainContext(owner.GetGrainId(), out var activation));
        var silo = _fixture.Cluster.Silos.Single(candidate => candidate.SiloAddress.Equals(activation.Address.SiloAddress));
        await WaitAsync(_fixture.Cluster.KillSiloAsync(silo, Token), owner, "immediate owner silo loss");
        await WaitAsync(_fixture.Cluster.WaitForLivenessToStabilizeAsync(didKill: true), owner, "membership convergence");

        var originalAfter = Assert.IsType<EffectRecord>(await owner.GetAsync(identity));
        var followUpAfter = Assert.IsType<EffectRecord>(await owner.GetAsync(followUpIdentity));
        Assert.Equal(EffectStatus.Failed, originalAfter.Status);
        Assert.Equal(failed.Evidence, originalAfter.Evidence);
        Assert.Equal(failed.Attempts, originalAfter.Attempts);
        Assert.Equal(followUp.ProviderKey, originalAfter.FollowUpEffectKey);
        Assert.NotEqual(originalAfter.ProviderKey, followUpAfter.ProviderKey);
        Assert.Equal(originalAfter.ProviderKey, followUpAfter.RelatedEffectKey);
        Assert.Equal(followUp.Authorization, followUpAfter.Authorization);
        Assert.Equal(EffectStatus.Pending, followUpAfter.Status);
        Assert.Empty(followUpAfter.Attempts);
        Assert.Equal(request, followUpAfter.Request);
        await AssertPersistedAsync(owner, originalAfter);
        await AssertPersistedAsync(owner, followUpAfter);
    }

    private async Task<(IExternalEffectGrain Owner, IRecipeEffectProviderGrain Provider, EffectIdentity Identity, EffectRequest Request)>
        CreateAsync(EffectKind kind, RecipeProviderBehavior behavior)
    {
        var account = $"scenario-provider-{Guid.NewGuid():N}";
        var owner = _fixture.Client.GetGrain<IExternalEffectGrain>($"order-{Guid.NewGuid():N}");
        var provider = _fixture.Client.GetGrain<IRecipeEffectProviderGrain>(account);
        await provider.ConfigureAsync(behavior);
        return (owner, provider, new("checkout", Guid.NewGuid(), "effect", 0),
            new(kind, account, "customer", "order confirmation", Amount: kind is EffectKind.PaymentAuthorization ? 20 : 0));
    }

    private void AdvanceTo(DateTimeOffset due)
    {
        _lastBarrier = $"advancing external-effects owner clock past {due:O}";
        _schedule = $"external-effect-next-action due {due:O}";
        _clock.Advance(due - _clock.GetUtcNow() + TimeSpan.FromSeconds(1));
    }

    private async Task AssertPersistedAsync(IExternalEffectGrain owner, EffectRecord expected)
    {
        _schedule = $"{expected.ProviderKey}: {expected.NextAction} due {expected.NextActionAt:O}, revision {expected.Revision}";
        var stored = (await ReadDictionaryAsync<EffectRecord>(owner.GetGrainId(), "external-effects"))[expected.ProviderKey];
        Assert.NotSame(expected, stored);
        Assert.Equal(expected.Identity, stored.Identity);
        Assert.Equal(expected.ProviderKey, stored.ProviderKey);
        Assert.Equal(expected.Request, stored.Request);
        Assert.Equal(expected.Policy, stored.Policy);
        Assert.Equal(expected.OnSuccessPolicy, stored.OnSuccessPolicy);
        Assert.Equal(expected.Status, stored.Status);
        Assert.Equal(expected.Revision, stored.Revision);
        Assert.Equal(expected.Attempts, stored.Attempts);
        Assert.Equal(expected.MayHaveApplied, stored.MayHaveApplied);
        Assert.Equal(expected.CancellationRequested, stored.CancellationRequested);
        Assert.Equal(expected.Evidence, stored.Evidence);
        Assert.Equal(expected.ResolutionOwner, stored.ResolutionOwner);
        Assert.Equal(expected.NextAction, stored.NextAction);
        Assert.Equal(expected.NextActionAt, stored.NextActionAt);
        Assert.Equal(expected.BusinessState, stored.BusinessState);
        Assert.Equal(expected.FollowUpEffectKey, stored.FollowUpEffectKey);
        Assert.Equal(expected.NextEffectKey, stored.NextEffectKey);
        Assert.Equal(expected.CancellationReason, stored.CancellationReason);
        Assert.Equal(expected.RelatedEffectKey, stored.RelatedEffectKey);
        Assert.Equal(expected.Authorization, stored.Authorization);
    }

    private async Task<Dictionary<string, T>> ReadDictionaryAsync<T>(GrainId grainId, string name)
    {
        var services = _fixture.Cluster.GetSiloServiceProvider();
        await using var manager = services.GetRequiredService<IJournaledStateManagerFactory>()
            .CreateStandalone(JournalId.FromGrainId(grainId));
        var reader = new DictionaryReader<T>(services.GetRequiredKeyedService<IDurableDictionaryCommandCodec<string, T>>(_format));
        manager.RegisterStateMachine(name, reader);
        await manager.InitializeAsync(Token);
        return reader.Values;
    }

    private async Task<T> WaitAsync<T>(Task<T> pending, IExternalEffectGrain owner, string phase)
    {
        try
        {
            var value = await pending.WaitAsync(Timeout, Token);
            _lastBarrier = phase;
            return value;
        }
        catch (TimeoutException exception)
        {
            var active = _fixture.Cluster.TryGetGrainContext(owner.GetGrainId(), out var context);
            throw new TimeoutException(
                $"Phase: {phase}; owner: {owner.GetGrainId()}; last barrier: {_lastBarrier}; pending: {pending.Status}; "
                + $"active: {active}; silo: {context?.Address.SiloAddress}; storage gate: {_gate.Entered.Status}; "
                + $"schedule: {_schedule}; owner clock: {_clock.GetUtcNow():O}; "
                + $"scheduler clock: {_fixture.JobClock.GetUtcNow():O}.", exception);
        }
    }

    private async Task WaitAsync(Task pending, IExternalEffectGrain owner, string phase) =>
        await WaitAsync(CompleteAsync(pending), owner, phase);

    private static async Task<bool> CompleteAsync(Task pending)
    {
        await pending;
        return true;
    }

    private sealed class DictionaryReader<T>(IDurableDictionaryCommandCodec<string, T> codec)
        : IStateMachine, IDurableDictionaryCommandHandler<string, T>
    {
        public Dictionary<string, T> Values { get; } = new(StringComparer.Ordinal);
        public void ApplySet(string key, T value) => Values[key] = value;
        public void ApplyRemove(string key) => Values.Remove(key);
        public void ApplyClear() => Values.Clear();
        public void Reset(int capacityHint) => Values.Clear();
        public void Reset(JournalStreamWriter writer) => Values.Clear();
        public void ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
            context.GetRequiredCommandCodec(entry.FormatKey, codec).Apply(entry.Reader, this);
        public void WritePendingEntries(JournalStreamWriter writer) { }
        public void WriteSnapshot(JournalStreamWriter writer) => throw new InvalidOperationException("Read-only recovery projection.");
    }
}

[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT"), TestProvider("None"), TestArea("DurableTasks")]
public sealed class JsonDurableTaskExternalEffectScenarioTests()
    : DurableTaskExternalEffectScenarioTests(DurableTaskRecoveryFixture.Json);

[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT"), TestProvider("None"), TestArea("DurableTasks")]
public sealed class OrleansBinaryDurableTaskExternalEffectScenarioTests()
    : DurableTaskExternalEffectScenarioTests(DurableTaskRecoveryFixture.OrleansBinary);

[JsonSerializable(typeof(EffectRecord))]
[JsonSerializable(typeof(EffectRequest))]
[JsonSerializable(typeof(ProviderResult))]
[JsonSerializable(typeof(RecipeProviderBehavior))]
[JsonSerializable(typeof(Guid))]
internal sealed partial class ExternalEffectScenarioJsonContext : JsonSerializerContext;
