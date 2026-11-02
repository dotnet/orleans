using Orleans;
using Orleans.Messaging;
using Orleans.Journaling;

namespace Messaging;

public interface IStockGrain : IGrainWithStringKey, IMessagingGrain
{
    Task InitializeAsync(int quantity);
    Task<StockSnapshot> GetSnapshotAsync();
}

public sealed class StockGrain(
    IInbox inbox,
    IOutbox outbox,
    IDurableStateManager state,
    StockMessages messages)

    : Grain, IStockGrain
{
    private readonly IDurableValue<Inventory> _inventory = state.GetOrAddState<IDurableValue<Inventory>>("stock");

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        inbox
            .Register(messages.Reserve, HandleReserveStock)
            .Register(messages.Restock, HandleRestock);
        return base.OnActivateAsync(cancellationToken);
    }

    public async Task InitializeAsync(int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(quantity);
        if (_inventory.Value is not null)
        {
            throw new InvalidOperationException("Stock has already been initialized.");
        }
        _inventory.Value = new(quantity, 0, 0);
        await state.WriteStateAsync();
    }

    public Task<StockSnapshot> GetSnapshotAsync() => Task.FromResult(new StockSnapshot(
        _inventory.Value ?? throw new InvalidOperationException("Initialize stock first.")));

    private void HandleReserveStock(ReserveStock request, IInboxHandlerContext context)
    {
        var inventory = _inventory.Value ?? throw new InvalidOperationException("Initialize stock first.");
        var commandId = context.Message.Envelope.MessageId;
        ReservationOutcome outcome = request.Quantity switch
        {
            <= 0 => new ReservationRejected(commandId, request.Quantity, inventory.Remaining,
                ReservationRejectionReason.InvalidQuantity),
            var quantity when quantity > inventory.Remaining => new ReservationRejected(commandId, quantity, inventory.Remaining,
                ReservationRejectionReason.InsufficientStock),
            var quantity => new ReservationAccepted(commandId, quantity, inventory.Remaining - quantity)
        };
        var reservations = inventory.Reservations;
        if (outcome is ReservationAccepted)
        {
            reservations = checked(reservations + 1);
        }
        var next = new Inventory(outcome.RemainingStock,
            reservations,
            checked(inventory.ProcessedRequests + 1));
        // SendReply encodes before staging; remaining changes run synchronously through return.
        outbox.SendReply(messages.Result, context, request.ReplyDestination, outcome);
        _inventory.Value = next;
        context.Complete();
    }

    private void HandleRestock(Restock request, IInboxHandlerContext context)
    {
        if (request.Quantity <= 0)
        {
            context.Fail("Restock quantity must be positive.");
            return;
        }
        var inventory = _inventory.Value ?? throw new InvalidOperationException("Initialize stock first.");
        var next = inventory with
        {
            Remaining = checked(inventory.Remaining + request.Quantity),
            ProcessedRequests = checked(inventory.ProcessedRequests + 1)
        };
        _inventory.Value = next;
        context.Complete();
    }
}
