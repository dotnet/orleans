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

public sealed class StockGrain : Grain, IStockGrain
{
    private readonly IDurableOutbox _outbox;
    private readonly IDurableStateManager _state;
    private readonly IDurableValue<Inventory> _inventory;
    private readonly DurableMessageType<ReservationOutcome> _result;
    private readonly DurableMessageWriter _writer;

    public StockGrain(IDurableInbox inbox, IDurableOutbox outbox,
        IDurableStateManager state, DurableMessageWriter writer,
        [FromKeyedServices(StockProtocol.Reserve)] DurableMessageType<ReserveStock> reserve,
        [FromKeyedServices(StockProtocol.Restock)] DurableMessageType<Restock> restock,
        [FromKeyedServices(StockProtocol.Result)] DurableMessageType<ReservationOutcome> result)
    {
        _outbox = outbox;
        _state = state;
        _writer = writer;
        _result = result;
        _inventory = state.GetOrAddState<IDurableValue<Inventory>>("stock");
        inbox.RegisterHandlers(routes => routes
            .Register(reserve, HandleReserveStock)
            .Register(restock, HandleRestock));
    }

    public async Task InitializeAsync(int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(quantity);
        if (_inventory.Value is not null)
        {
            throw new InvalidOperationException("Stock has already been initialized.");
        }
        _inventory.Value = new(quantity, 0, 0);
        await _state.WriteStateAsync();
    }

    public Task<StockSnapshot> GetSnapshotAsync() => Task.FromResult(new StockSnapshot(
        _inventory.Value ?? throw new InvalidOperationException("Initialize stock first.")));

    private ValueTask HandleReserveStock(ReserveStock request, IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.Quantity);
        var inventory = _inventory.Value ?? throw new InvalidOperationException("Initialize stock first.");
        var accepted = request.Quantity <= inventory.Remaining;
        var outcome = new ReservationOutcome(context.Envelope.MessageId, request.Quantity, accepted,
            accepted ? inventory.Remaining - request.Quantity : inventory.Remaining);
        var next = new Inventory(outcome.RemainingStock,
            checked(inventory.Reservations + (accepted ? 1 : 0)),
            checked(inventory.ProcessedRequests + 1));
        using var reply = _writer.Create(_result, context.Envelope.MessageId.CreateChildKey("result"),
            request.ReplyDestination, outcome);
        cancellationToken.ThrowIfCancellationRequested();

        // Final block: all fallible preparation is finished. No await through handler return.
        // Inventory, reply intent, and inbox completion share one journal write.
        _inventory.Value = next;
        _outbox.Send(reply);
        context.Complete();
        return ValueTask.CompletedTask;
    }

    private ValueTask HandleRestock(Restock request, IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.Quantity);
        var inventory = _inventory.Value ?? throw new InvalidOperationException("Initialize stock first.");
        var next = inventory with
        {
            Remaining = checked(inventory.Remaining + request.Quantity),
            ProcessedRequests = checked(inventory.ProcessedRequests + 1)
        };
        cancellationToken.ThrowIfCancellationRequested();

        _inventory.Value = next;
        context.Complete();
        return ValueTask.CompletedTask;
    }
}
