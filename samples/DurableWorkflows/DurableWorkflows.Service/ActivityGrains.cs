using Orleans.DurableTasks;
using Orleans.Journaling;

namespace DurableWorkflows;

// Each durable task method's state change commits in the same journal write as its result. The grains record every
// effect by operation ID and return the recorded effect for a repeated operation, so a retried or repeated workflow
// applies each effect once.
public sealed class ActivityGrain : DurableGrain, IActivityGrain
{
    private readonly IDurableDictionary<string, ActivityResult> _effects;

    public ActivityGrain() => _effects = StateManager.GetOrAddDictionary<string, ActivityResult>("effects");

    public async DurableTask<ActivityResult> ExecuteAsync(ActivityRequest request)
    {
        if (_effects.TryGetValue(request.OperationId, out var existing))
        {
            return existing;
        }

        if (request.Fail)
        {
            throw new InvalidOperationException($"Injected failure for {request.OperationId}.");
        }

        var taskId = DurableTask.Current.Id.ToString();
        var result = new ActivityResult(request.OperationId, request.Input.ToUpperInvariant(), taskId);
        _effects[request.OperationId] = result;
        return result;
    }

    public Task<ActivitySnapshot> GetSnapshotAsync() =>
        Task.FromResult(new ActivitySnapshot(new Dictionary<string, ActivityResult>(_effects)));
}

public sealed class InventoryGrain : DurableGrain, IInventoryGrain
{
    private readonly IDurableDictionary<string, SagaEffect> _effects;

    public InventoryGrain() => _effects = StateManager.GetOrAddDictionary<string, SagaEffect>("effects");

    public async DurableTask<SagaEffect> ReserveAsync(string operationId) => _effects.ApplyOnce(operationId, "reserve-inventory");

    public async DurableTask<SagaEffect> ReleaseAsync(string operationId) => _effects.ApplyOnce(operationId, "release-inventory");

    public Task<SagaParticipantSnapshot> GetSnapshotAsync() => Task.FromResult(_effects.ToSnapshot());
}

public sealed class PaymentGrain : DurableGrain, IPaymentGrain
{
    private readonly IDurableDictionary<string, SagaEffect> _effects;

    public PaymentGrain() => _effects = StateManager.GetOrAddDictionary<string, SagaEffect>("effects");

    public async DurableTask<SagaEffect> ChargeAsync(string operationId) => _effects.ApplyOnce(operationId, "charge-payment");

    public async DurableTask<SagaEffect> RefundAsync(string operationId) => _effects.ApplyOnce(operationId, "refund-payment");

    public Task<SagaParticipantSnapshot> GetSnapshotAsync() => Task.FromResult(_effects.ToSnapshot());
}

file static class SagaEffects
{
    public static SagaEffect ApplyOnce(this IDurableDictionary<string, SagaEffect> effects, string operationId, string kind)
    {
        if (!effects.TryGetValue(operationId, out var effect))
        {
            effect = new SagaEffect(operationId, kind);
            effects[operationId] = effect;
        }

        return effect;
    }

    public static SagaParticipantSnapshot ToSnapshot(this IDurableDictionary<string, SagaEffect> effects) =>
        new(new Dictionary<string, SagaEffect>(effects));
}
