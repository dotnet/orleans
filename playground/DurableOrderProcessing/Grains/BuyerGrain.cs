using Orleans.DurableTasks;
using Orleans.Journaling;

namespace DurableOrderProcessing.Grains;

public interface IBuyerGrain : IGrainWithStringKey
{
    Task<OrderSummary[]> GetOrdersAsync();

    DurableTask RecordOrderAsync(OrderSummary summary);
}

// A per-buyer projection of order status, which serves the buyer's order list.
// Each order's workflow records its status changes with detached durable calls.
public sealed class BuyerGrain : DurableGrain, IBuyerGrain
{
    private readonly IDurableDictionary<Guid, OrderSummary> _orders;

    public BuyerGrain()
    {
        _orders = StateManager.GetOrAddDictionary<Guid, OrderSummary>("orders");
    }

    public Task<OrderSummary[]> GetOrdersAsync() =>
        Task.FromResult(_orders.Values.OrderByDescending(order => order.OrderDate).ToArray());

    // The change commits with the task's result.
    public async DurableTask RecordOrderAsync(OrderSummary summary)
    {
        // An order's workflow detaches each change, so changes arrive in any order; the highest version is the latest.
        if (!_orders.TryGetValue(summary.OrderId, out var current) || current.Version < summary.Version)
        {
            _orders[summary.OrderId] = summary;
        }
    }
}
