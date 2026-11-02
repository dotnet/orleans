using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
using Orleans.Journaling;
using Orleans.Messaging;

namespace OrderProcessing.Grains;

public interface IBasketGrain : IGrainWithStringKey
{
    Task<BasketItem[]> GetAsync();

    Task<BasketItem[]> SetQuantityAsync(int productId, int quantity);

    Task<Guid?> CheckoutAsync(CheckoutRequest request);
}

// The buyer's basket. Checkout clears the basket and stages the order submission in one journal write,
// which replaces eShop's integration event log and its transactional outbox.
public sealed class BasketGrain : DurableGrain, IBasketGrain
{
    private static readonly TimeSpan CheckoutRetention = TimeSpan.FromDays(7);

    private readonly IOutbox _outbox;
    private readonly OrderMessages _messages;
    private readonly TimeProvider _timeProvider;
    private readonly TimeProvider _jobClock;
    private readonly IDurableDictionary<int, BasketItem> _items;
    private readonly IDurableDictionary<Guid, DateTimeOffset> _checkouts;

    public BasketGrain(
        IOutbox outbox,
        OrderMessages messages,
        TimeProvider timeProvider,
        [FromKeyedServices(DurableJobTimeProviderNames.DurableJobs)] TimeProvider jobClock)
    {
        _outbox = outbox;
        _messages = messages;
        _timeProvider = timeProvider;
        _jobClock = jobClock;
        _items = StateManager.GetOrAddDictionary<int, BasketItem>("items");
        _checkouts = StateManager.GetOrAddDictionary<Guid, DateTimeOffset>("checkouts");
    }

    public Task<BasketItem[]> GetAsync() => Task.FromResult(_items.Values.ToArray());

    public async Task<BasketItem[]> SetQuantityAsync(int productId, int quantity)
    {
        if (quantity > 0)
        {
            var product = await GrainFactory.GetGrain<ICatalogItemGrain>(productId).GetAsync()
                ?? throw new ArgumentException($"Product {productId} does not exist.", nameof(productId));
            _items[productId] = new BasketItem(productId, product.Name, product.Price, quantity);
        }
        else
        {
            _items.Remove(productId);
        }

        await WriteStateAsync();
        return _items.Values.ToArray();
    }

    // The request ID becomes the order ID. The basket remembers its checkouts for CheckoutRetention, so a retried
    // checkout within that period returns the same order, even after later checkouts. A retry writes state first, so
    // a checkout whose earlier write failed is persisted before its order ID is returned.
    public async Task<Guid?> CheckoutAsync(CheckoutRequest request)
    {
        if (_checkouts.ContainsKey(request.RequestId))
        {
            await WriteStateAsync();
            return request.RequestId;
        }

        if (_items.Count == 0)
        {
            return null;
        }

        var orderId = request.RequestId;
        var lines = _items.Values
            .Select(item => new OrderLine(item.ProductId, item.ProductName, item.UnitPrice, item.Quantity))
            .ToArray();
        var now = _timeProvider.GetUtcNow();
        _outbox.Send(
            _messages.SubmitOrder,
            MessageId.Create("orders", orderId.ToString("N"), "submit"),
            GrainFactory.GetGrain<IOrderGrain>(orderId),
            new SubmitOrder(this.GetPrimaryKeyString(), lines, request.Address, request.CardNumber[^4..], _jobClock.GetUtcNow()));
        _items.Clear();
        foreach (var id in _checkouts.Where(pair => now - pair.Value >= CheckoutRetention).Select(pair => pair.Key).ToArray())
        {
            _checkouts.Remove(id);
        }

        _checkouts[orderId] = now;
        await WriteStateAsync();
        return orderId;
    }
}
