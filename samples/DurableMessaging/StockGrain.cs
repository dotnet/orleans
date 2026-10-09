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

public sealed class StockGrain : Grain, IStockGrain, IInboxHandler
{
    private readonly IDurableOutbox _outbox;
    private readonly IDurableStateManager _state;
    private readonly IDurableValue<Inventory> _inventory;
    private readonly DurableMessageType<ReserveStock> _reserve;
    private readonly DurableMessageType<ReservationOutcome> _result;
    private readonly DurableMessageWriter _writer;

    public StockGrain(IDurableInbox inbox, IDurableOutbox outbox,
        IDurableStateManager state, DurableMessageWriter writer,
        [FromKeyedServices(StockProtocol.Reserve)] DurableMessageType<ReserveStock> reserve,
        [FromKeyedServices(StockProtocol.Result)] DurableMessageType<ReservationOutcome> result)
    {
        _outbox = outbox;
        _state = state;
        _writer = writer;
        _reserve = reserve;
        _result = result;
        _inventory = state.GetOrAddState<IDurableValue<Inventory>>("stock");
        inbox.RegisterHandler(this);
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

    public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        var request = _reserve.Decode(context.Envelope);
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
}
