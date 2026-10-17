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

    public CatalogItemGrain(IInbox inbox, IOutbox outbox, OrderMessages messages)
    {
        _outbox = outbox;
        _messages = messages;
        _item = StateManager.GetOrAddValue<CatalogItem>("item");
        _reservations = StateManager.GetOrAddDictionary<Guid, int>("reservations");
        inbox.Register(messages.ReserveStock, HandleReserve)
            .Register(messages.ReleaseStock, HandleRelease)
            .Register(messages.CommitStock, HandleCommit);
    }

    private int ProductId => (int)this.GetPrimaryKeyLong();

    // The journal holds the item once its stock first changes; until then, it is the seed entry.
    private CatalogItem? Item => _item.Value ?? CatalogData.Find(ProductId);

    public Task<CatalogItem?> GetAsync() => Task.FromResult(Item);

    private void HandleReserve(ReserveStock request, IInboxHandlerContext context)
    {
        var item = Item;
        var confirmed = item is not null && request.Units > 0 && item.AvailableStock >= request.Units;
        if (confirmed)
        {
            _item.Value = item! with
            {
                AvailableStock = item.AvailableStock - request.Units,
                ReservedStock = item.ReservedStock + request.Units,
            };
            _reservations[request.OrderId] = request.Units;
        }

        _outbox.SendReply(_messages.StockResult, context, request.ReplyTo, new StockResult(ProductId, confirmed));
        context.Complete();
    }

    private void HandleRelease(ReleaseStock request, IInboxHandlerContext context)
    {
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
