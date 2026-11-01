using Orleans.DurableTasks;
using Orleans.Journaling;

namespace DurableOrderProcessing.Grains;

public interface ICatalogItemGrain : IGrainWithIntegerKey
{
    Task<CatalogItem?> GetAsync();

    DurableTask<bool> ReserveAsync(Guid orderId, int units);

    // Returns whether the order held a reservation, so releasing is idempotent.
    DurableTask<bool> ReleaseAsync(Guid orderId);

    DurableTask CommitAsync(Guid orderId);
}

// One grain per product owns its stock. Orders reserve stock while they await validation,
// commit it once paid, and release it when cancelled. Each operation's state change commits with its result.
// Reservation outcomes retain each order's effect independently of durable-task retention.
public sealed class CatalogItemGrain : DurableGrain, ICatalogItemGrain
{
    private readonly IDurableValue<CatalogItem> _item;
    private readonly IDurableDictionary<Guid, int> _reservations;
    private readonly IDurableDictionary<Guid, StockReservation> _reservationOutcomes;

    public CatalogItemGrain()
    {
        _item = StateManager.GetOrAddValue<CatalogItem>("item");
        _reservations = StateManager.GetOrAddDictionary<Guid, int>("reservations");
        _reservationOutcomes = StateManager.GetOrAddDictionary<Guid, StockReservation>("reservation-outcomes");
    }

    private int ProductId => (int)this.GetPrimaryKeyLong();

    // The journal holds the item once its stock first changes; until then, it is the seed entry.
    private CatalogItem? Item => _item.Value ?? CatalogData.Find(ProductId);

    public Task<CatalogItem?> GetAsync() => Task.FromResult(Item);

    public async DurableTask<bool> ReserveAsync(Guid orderId, int units)
    {
        if (_reservationOutcomes.TryGetValue(orderId, out var outcome))
        {
            if (outcome.Units != units)
            {
                throw new InvalidOperationException($"Order {orderId} already requested a different reservation quantity.");
            }

            return outcome.Confirmed;
        }

        var item = Item;
        var confirmed = item is not null && units > 0 && item.AvailableStock >= units;
        if (confirmed)
        {
            _item.Value = item! with
            {
                AvailableStock = item.AvailableStock - units,
                ReservedStock = item.ReservedStock + units,
            };
            _reservations[orderId] = units;
        }

        _reservationOutcomes[orderId] = new(units, confirmed);
        return confirmed;
    }

    public async DurableTask<bool> ReleaseAsync(Guid orderId)
    {
        if (!_reservations.Remove(orderId, out var units))
        {
            return false;
        }

        var item = Item!;
        _item.Value = item with
        {
            AvailableStock = item.AvailableStock + units,
            ReservedStock = item.ReservedStock - units,
        };
        return true;
    }

    public async DurableTask CommitAsync(Guid orderId)
    {
        if (_reservations.Remove(orderId, out var units))
        {
            var item = Item!;
            _item.Value = item with
            {
                ReservedStock = item.ReservedStock - units,
                SoldStock = item.SoldStock + units,
            };
        }
    }
}
