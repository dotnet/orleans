using Orleans;
using Orleans.DurableMessaging;
using Orleans.Journaling;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;

namespace DurableMessaging;

public interface IStockGrain : IGrainWithStringKey, IDurableMessagingGrain
{
    Task InitializeAsync(int quantity);
    Task<StockSnapshot> GetSnapshotAsync();
}

public sealed class StockGrain : Grain, IStockGrain, IInboxHandler, IDisposable
{
    private readonly IDurableOutbox _outbox;
    private readonly IDurableStateManager _state;
    private readonly IDurableValue<Inventory> _inventory;
    private readonly IDurableDictionary<HierarchicalKey, ReservationOutcome> _ledger;
    private readonly Serializer<StockMessage> _serializer;
    private readonly ArcBufferWriter _encoder = new();

    public StockGrain(IDurableInbox inbox, IDurableOutbox outbox,
        IDurableStateManager state, Serializer<StockMessage> serializer)
    {
        _outbox = outbox;
        _state = state;
        _serializer = serializer;
        _inventory = state.GetOrAddState<IDurableValue<Inventory>>("stock");
        _ledger = state.GetOrAddState<IDurableDictionary<HierarchicalKey, ReservationOutcome>>("reservations");
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
        _inventory.Value ?? throw new InvalidOperationException("Initialize stock first."), _ledger.Count));

    public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        // The inbox envelope is borrowed: decode it, but never dispose it.
        if (_serializer.Deserialize(context.Envelope.Payload) is not ReserveStock request)
        {
            throw new ArgumentException("The stock grain only accepts reservation requests.");
        }
        ArgumentNullException.ThrowIfNull(request.Operation);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.Quantity);
        var inventory = _inventory.Value ?? throw new InvalidOperationException("Initialize stock first.");
        var duplicate = _ledger.TryGetValue(request.Operation, out var original);
        if (duplicate && original!.Quantity != request.Quantity)
        {
            throw new ArgumentException("A business-operation key cannot be reused for a different quantity.");
        }

        var accepted = request.Quantity <= inventory.Remaining;
        var outcome = original ?? new ReservationOutcome(request.Operation, request.Quantity, accepted,
            accepted ? Guid.NewGuid() : Guid.Empty,
            accepted ? inventory.Remaining - request.Quantity : inventory.Remaining);
        var next = new Inventory(outcome.Accepted && !duplicate ? outcome.RemainingStock : inventory.Remaining,
            checked(inventory.Reservations + (!duplicate && outcome.Accepted ? 1 : 0)),
            checked(inventory.ProcessedRequests + 1));
        using var reply = StockProtocol.Encode(_serializer, _encoder,
            this.GetGrainId(), request.ReplyDestination, outcome);
        cancellationToken.ThrowIfCancellationRequested();

        // Final block: all fallible preparation is finished. No await through handler return.
        // The business ledger, inventory, reply intent, and inbox completion share one journal write.
        if (!duplicate)
        {
            _ledger.Add(request.Operation, outcome);
        }
        _inventory.Value = next;
        _outbox.Send(reply);
        context.Complete();
        return ValueTask.CompletedTask;
    }

    public void Dispose() => _encoder.Dispose();
}
