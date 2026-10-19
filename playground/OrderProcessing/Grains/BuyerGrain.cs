using Orleans.Journaling;
using Orleans.Messaging;

namespace OrderProcessing.Grains;

public interface IBuyerGrain : IGrainWithStringKey
{
    Task<OrderSummary[]> GetOrdersAsync();
}

// A per-buyer projection of order status messages, which serves the buyer's order list.
public sealed class BuyerGrain : DurableGrain, IBuyerGrain
{
    private readonly IDurableDictionary<Guid, OrderSummary> _orders;

    public BuyerGrain(IInbox inbox, OrderMessages messages)
    {
        _orders = StateManager.GetOrAddDictionary<Guid, OrderSummary>("orders");
        inbox.Register(messages.StatusChanged, HandleStatusChanged);
    }

    public Task<OrderSummary[]> GetOrdersAsync() =>
        Task.FromResult(_orders.Values.OrderByDescending(order => order.OrderDate).ToArray());

    private void HandleStatusChanged(OrderSummary summary, IInboxHandlerContext context)
    {
        // Status messages can arrive out of order; the highest version is the latest.
        if (!_orders.TryGetValue(summary.OrderId, out var current) || current.Version < summary.Version)
        {
            _orders[summary.OrderId] = summary;
        }

        context.Complete();
    }
}
