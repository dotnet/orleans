using Orleans.Journaling;
using Orleans.Messaging;

namespace OrderProcessing.Grains;

public interface ICatalogItemGrain : IGrainWithIntegerKey
{
    Task<CatalogItem?> GetAsync();
}

// One grain per product owns its stock. Orders reserve stock while they await validation,
// commit it once paid, and release it when cancelled.
public sealed class CatalogItemGrain : DurableGrain, ICatalogItemGrain
{
    private readonly IOutbox _outbox;
    private readonly OrderMessages _messages;
    private readonly IDurableValue<CatalogItem> _item;
    private readonly IDurableDictionary<Guid, int> _reservations;
    private readonly IDurableDictionary<Guid, StockReservation> _reservationOutcomes;

    public CatalogItemGrain(IInbox inbox, IOutbox outbox, OrderMessages messages)
    {
        _outbox = outbox;
        _messages = messages;
        _item = StateManager.GetOrAddValue<CatalogItem>("item");
        _reservations = StateManager.GetOrAddDictionary<Guid, int>("reservations");
        _reservationOutcomes = StateManager.GetOrAddDictionary<Guid, StockReservation>("reservation-outcomes");
        inbox.Register(messages.ReserveStock, HandleReserve)
            .Register(messages.ReleaseStock, HandleRelease)
            .Register(messages.CommitStock, HandleCommit);
    }

    private int ProductId => (int)this.GetPrimaryKeyLong();

    // The journal holds the item once its stock first changes; until then, it is the seed entry.
    private CatalogItem? Item => _item.Value ?? CatalogData.Find(ProductId);

    public Task<CatalogItem?> GetAsync() => Task.FromResult(Item);

    // The reply is staged before stock changes, so a failed send leaves stock untouched for the retry.
    // Each order retains its outcome after release or commit, independently of inbox deduplication.
    private void HandleReserve(ReserveStock request, IInboxHandlerContext context)
    {
        if (_reservationOutcomes.TryGetValue(request.OrderId, out var outcome))
        {
            if (outcome.Units is { } units && units != request.Units)
            {
                context.Fail($"Order {request.OrderId} already requested a different reservation quantity.");
                return;
            }

            _outbox.SendReply(_messages.StockResult, context, request.ReplyTo, new StockResult(ProductId, outcome.Confirmed));
            context.Complete();
            return;
        }

        var item = Item;
        var reserved = _reservations.ContainsKey(request.OrderId);
        var reserve = !reserved && item is not null && request.Units > 0 && item.AvailableStock >= request.Units;
        _outbox.SendReply(_messages.StockResult, context, request.ReplyTo, new StockResult(ProductId, reserved || reserve));

        if (reserve)
        {
            _item.Value = item! with
            {
                AvailableStock = item.AvailableStock - request.Units,
                ReservedStock = item.ReservedStock + request.Units,
            };
            _reservations[request.OrderId] = request.Units;
        }

        _reservationOutcomes[request.OrderId] = new(request.Units, reserved || reserve);
        context.Complete();
    }

    private void HandleRelease(ReleaseStock request, IInboxHandlerContext context)
    {
        // A release which overtakes the reserve records a terminal rejection for that order.
        _reservationOutcomes.TryAdd(request.OrderId, new(null, false));
        if (_reservations.Remove(request.OrderId, out var units))
        {
            var item = Item!;
            _item.Value = item with
            {
                AvailableStock = item.AvailableStock + units,
                ReservedStock = item.ReservedStock - units,
            };
        }

        context.Complete();
    }

    private void HandleCommit(CommitStock request, IInboxHandlerContext context)
    {
        _reservationOutcomes.TryAdd(request.OrderId, new(null, false));
        if (_reservations.Remove(request.OrderId, out var units))
        {
            var item = Item!;
            _item.Value = item with
            {
                ReservedStock = item.ReservedStock - units,
                SoldStock = item.SoldStock + units,
            };
        }

        context.Complete();
    }
}
