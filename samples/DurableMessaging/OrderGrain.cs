using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.DurableMessaging;
using Orleans.Journaling;
using Orleans.Runtime;

namespace DurableMessaging;

public interface IOrderGrain : IGrainWithStringKey, IDurableMessagingGrain
{
    Task<HierarchicalKey> ReserveAsync(GrainId stock, HierarchicalKey commandId, int quantity);
    Task<DeliveryResult> ResubmitAsync(GrainId stock, HierarchicalKey commandId, int quantity);
}

public sealed class OrderGrain(
    IDurableInbox inbox,
    IDurableOutbox outbox,
    IDurableStateManager state,
    IJournaledStateManager journal,
    [FromKeyedServices(StockProtocol.Reserve)] DurableMessageType<ReserveStock> reserve,
    [FromKeyedServices(StockProtocol.Result)] DurableMessageType<ReservationOutcome> result,
    CommittedReceiptsProbe probe)
    : Grain, IOrderGrain, IJournaledStateHook
{
    private readonly IDurableList<ReservationOutcome> _receipts = state.GetOrAddState<IDurableList<ReservationOutcome>>("receipts");
    private ReservationOutcome[] _captured = [];

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        inbox.RegisterHandlers(routes => routes.Register(result, this,
            static (outcome, grain, context) => grain.HandleResult(outcome, context)));
        journal.Hooks.Add(this);
        return base.OnActivateAsync(cancellationToken);
    }

    public async Task<HierarchicalKey> ReserveAsync(GrainId stock, HierarchicalKey commandId, int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        outbox.Send(reserve, commandId, stock, new ReserveStock(quantity, this.GetGrainId()));
        await state.WriteStateAsync(); // Ordinary callers explicitly await intent persistence.
        return commandId;
    }

    public async Task<DeliveryResult> ResubmitAsync(GrainId stock, HierarchicalKey commandId, int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        using var request = reserve.Create(commandId, outbox.SenderId, stock,
            new ReserveStock(quantity, this.GetGrainId()));
        // Explicit admission exposes the duplicate result after the original reply's ACK.
        return await GrainFactory.GetGrain<IDurableInboxExtension>(stock).DeliverAsync(request);
    }

    private void HandleResult(ReservationOutcome outcome, IInboxHandlerContext context)
    {
        if (context.Envelope.MessageId != outcome.CommandId.CreateChildKey("result"))
        {
            throw new ArgumentException("The reply must identify the original reservation command.");
        }
        _receipts.Add(outcome);
        context.Complete();
    }

    // Capture the receipt snapshot before persistence and report it after acknowledgement.
    public ValueTask BeforeOperationAsync(JournaledStateOperation operation, CancellationToken cancellationToken)
    {
        _captured = _receipts.ToArray();
        return ValueTask.CompletedTask;
    }

    public ValueTask AfterOperationAsync(JournaledStateOperation operation, CancellationToken cancellationToken)
    {
        if (operation is JournaledStateOperation.Write or JournaledStateOperation.Snapshot)
        {
            probe.OnAcknowledged(_captured);
        }
        _captured = [];
        return ValueTask.CompletedTask;
    }
}

// The after-ack hook releases the host-local observer when the original reply is committed.
public sealed class CommittedReceiptsProbe
{
    private readonly TaskCompletionSource<ReservationOutcome[]> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<ReservationOutcome[]> Completion => _completion.Task;

    public void OnAcknowledged(ReservationOutcome[] receipts)
    {
        if (receipts.Length >= 1)
        {
            _completion.TrySetResult(receipts);
        }
    }
}
