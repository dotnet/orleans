using System.Distributed.DurableTasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;

namespace DurableWorkflows;

public sealed class ActivityGrain(
    [FromKeyedServices("effects")] IDurableDictionary<string, ActivityResult> effects)
    : DurableGrain, IActivityGrain
{
    public DurableTask<ActivityResult> ExecuteAsync(ActivityRequest request) =>
        DurableTask.Run(
            static (state, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (state.State.TryGetValue(state.Request.OperationId, out var existing))
                {
                    return existing;
                }

                if (state.Request.Fail)
                {
                    throw new InvalidOperationException($"Injected failure for {state.Request.OperationId}.");
                }

                var result = new ActivityResult(state.Request.OperationId, state.Request.Input.ToUpperInvariant());
                state.State[state.Request.OperationId] = result;
                return result;
            },
            (State: effects, Request: request));

    public Task<ActivitySnapshot> GetSnapshotAsync() =>
        Task.FromResult(new ActivitySnapshot(new Dictionary<string, ActivityResult>(effects)));
}

public sealed class ApprovalGrain(
    [FromKeyedServices("decision")] IDurableTaskCompletionSource<ApprovalDecision> decision)
    : DurableGrain, IApprovalGrain
{
    public DurableTask<ApprovalDecision> WaitForDecisionAsync(string correlationId)
    {
        if (!string.Equals(correlationId, this.GetPrimaryKeyString(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The approval correlation id must match the approval grain id.");
        }

        return DurableTask.Run(token => decision.Task.WaitAsync(token));
    }

    public async Task SubmitDecisionAsync(ApprovalDecision value)
    {
        if (!string.Equals(value.CorrelationId, this.GetPrimaryKeyString(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The submitted correlation id does not match this approval.");
        }

        if (!decision.TrySetResult(value) && decision.State.Value != value)
        {
            throw new InvalidOperationException("A different decision has already been recorded for this correlation id.");
        }

        await WriteStateAsync();
    }

    public Task<ApprovalSnapshot> GetSnapshotAsync() =>
        Task.FromResult(new ApprovalSnapshot(this.GetPrimaryKeyString(), decision.State.Value));
}

public sealed class CancellationGrain(
    [FromKeyedServices("cancellation")] IDurableTaskCompletionSource<CancellationSignal> cancellation)
    : DurableGrain, ICancellationGrain
{
    public DurableTask<CancellationSignal> WaitForCancellationAsync(string cancellationId)
    {
        if (!string.Equals(cancellationId, this.GetPrimaryKeyString(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The cancellation id must match the cancellation grain id.");
        }

        return DurableTask.Run(token => cancellation.Task.WaitAsync(token));
    }

    public async Task RequestCancellationAsync(CancellationSignal signal)
    {
        if (!string.Equals(signal.CancellationId, this.GetPrimaryKeyString(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The requested cancellation id does not match this cancellation.");
        }

        if (!cancellation.TrySetResult(signal) && cancellation.State.Value != signal)
        {
            throw new InvalidOperationException("A different cancellation has already been recorded for this id.");
        }

        await WriteStateAsync();
    }

    public Task<CancellationSnapshot> GetSnapshotAsync() =>
        Task.FromResult(new CancellationSnapshot(this.GetPrimaryKeyString(), cancellation.State.Value));
}

public sealed class InventoryGrain(
    [FromKeyedServices("inventory-effects")] IDurableDictionary<string, SagaEffect> effects)
    : DurableGrain, IInventoryGrain
{
    public DurableTask<SagaEffect> ReserveAsync(string operationId) => Apply(operationId, "reserve-inventory");
    public DurableTask<SagaEffect> ReleaseAsync(string operationId) => Apply(operationId, "release-inventory");
    public Task<SagaParticipantSnapshot> GetSnapshotAsync() => GetSnapshot(effects);

    private DurableTask<SagaEffect> Apply(string operationId, string kind) =>
        DurableTask.Run(
            static (state, cancellationToken) =>
                SagaEffects.ApplyOnce(state.Effects, state.OperationId, state.Kind, cancellationToken),
            (Effects: effects, OperationId: operationId, Kind: kind));

    private static Task<SagaParticipantSnapshot> GetSnapshot(IDurableDictionary<string, SagaEffect> state) =>
        SagaEffects.GetSnapshot(state);
}

public sealed class PaymentGrain(
    [FromKeyedServices("payment-effects")] IDurableDictionary<string, SagaEffect> effects)
    : DurableGrain, IPaymentGrain
{
    public DurableTask<SagaEffect> ChargeAsync(string operationId) => Apply(operationId, "charge-payment");
    public DurableTask<SagaEffect> RefundAsync(string operationId) => Apply(operationId, "refund-payment");
    public Task<SagaParticipantSnapshot> GetSnapshotAsync() => GetSnapshot(effects);

    private DurableTask<SagaEffect> Apply(string operationId, string kind) =>
        DurableTask.Run(
            static (state, cancellationToken) =>
                SagaEffects.ApplyOnce(state.Effects, state.OperationId, state.Kind, cancellationToken),
            (Effects: effects, OperationId: operationId, Kind: kind));

    private static Task<SagaParticipantSnapshot> GetSnapshot(IDurableDictionary<string, SagaEffect> state) =>
        SagaEffects.GetSnapshot(state);
}

file static class SagaEffects
{
    public static SagaEffect ApplyOnce(
        IDurableDictionary<string, SagaEffect> effects,
        string operationId,
        string kind,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (effects.TryGetValue(operationId, out var existing))
        {
            return existing;
        }

        var effect = new SagaEffect(operationId, kind);
        effects[operationId] = effect;
        return effect;
    }

    public static Task<SagaParticipantSnapshot> GetSnapshot(IDurableDictionary<string, SagaEffect> effects) =>
        Task.FromResult(new SagaParticipantSnapshot(new Dictionary<string, SagaEffect>(effects)));
}
