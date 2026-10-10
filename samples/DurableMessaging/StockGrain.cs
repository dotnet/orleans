using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.DurableMessaging;
using Orleans.Journaling;

namespace DurableMessaging;

public interface IStockGrain : IGrainWithStringKey, IDurableMessagingGrain
{
    Task InitializeAsync(int quantity);
    Task<StockSnapshot> GetSnapshotAsync();
}

public sealed class StockGrain(
    IDurableInbox inbox,
    IDurableOutbox outbox,
    IDurableStateManager state,
    [FromKeyedServices(StockProtocol.Reserve)] DurableMessageType<ReserveStock> reserve,
    [FromKeyedServices(StockProtocol.Restock)] DurableMessageType<Restock> restock,
    [FromKeyedServices(StockProtocol.Result)] DurableMessageType<ReservationOutcome> result)
    : Grain, IStockGrain
{
    private readonly IDurableValue<Inventory> _inventory = state.GetOrAddState<IDurableValue<Inventory>>("stock");

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        inbox.RegisterHandlers(routes => routes
            .Register(reserve, this, static (request, grain, context) => grain.HandleReserveStock(request, context))
            .Register(restock, this, static (request, grain, context) => grain.HandleRestock(request, context)));
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
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.Quantity);
        var inventory = _inventory.Value ?? throw new InvalidOperationException("Initialize stock first.");
        var accepted = request.Quantity <= inventory.Remaining;
        var outcome = new ReservationOutcome(context.Envelope.MessageId, request.Quantity, accepted,
            accepted ? inventory.Remaining - request.Quantity : inventory.Remaining);
        var next = new Inventory(outcome.RemainingStock,
            checked(inventory.Reservations + (accepted ? 1 : 0)),
            checked(inventory.ProcessedRequests + 1));
        // SendReply encodes before staging; remaining changes run synchronously through return.
        outbox.SendReply(result, context, request.ReplyDestination, outcome);
        _inventory.Value = next;
        context.Complete();
    }

    private void HandleRestock(Restock request, IInboxHandlerContext context)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.Quantity);
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
