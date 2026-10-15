using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Concurrency;
using Orleans.CodeGeneration;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Invocation;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;
using static Documentation.Grains.RequestScheduling.CatalogGrainHelpers;

namespace Documentation.Grains.Versioning.Compatibility
{
    [GenerateSerializer]
    public sealed record ReservationResult(
        [property: Id(0)] string ReservationId);

    // <versioned_inventory_interface>
[Version(2)]
public interface IInventoryGrain : IGrainWithStringKey
{
    // Retained for version 1 callers.
    Task<int> ReserveAsync(string sku, int quantity);

    // Added for version 2 callers.
    Task<ReservationResult> ReserveWithIdAsync(
        string operationId,
        string sku,
        int quantity);
}
    // </versioned_inventory_interface>
}

namespace Documentation.Grains.Versioning.Deployment
{
    internal static class VersioningConfiguration
    {
        internal static void Configure(ISiloBuilder siloBuilder)
        {
            // <configure_grain_versioning>
siloBuilder.Configure<GrainVersioningOptions>(options =>
{
    options.DefaultCompatibilityStrategy = nameof(BackwardCompatible);
    options.DefaultVersionSelectorStrategy = nameof(AllCompatibleVersions);
});
            // </configure_grain_versioning>
        }
    }
}

namespace Documentation.Grains.Versioning.Interfaces
{
    [GenerateSerializer]
    public sealed record Cart(
        [property: Id(0)] IReadOnlyList<Item> Items);

    [GenerateSerializer]
    public sealed record Item(
        [property: Id(0)] string ProductId,
        [property: Id(1)] int Quantity);

    // <versioned_cart_interface>
[Version(2)]
public interface ICartGrain : IGrainWithStringKey
{
    Task<Cart> GetAsync();
    Task AddAsync(Item item);
}
    // </versioned_cart_interface>
}

namespace Documentation.Grains.RequestContextExamples
{
    public interface IOrderGrain : IGrainWithStringKey
    {
        Task Submit();
    }

    internal static class OrderCaller
    {
        internal static async Task Submit(IGrainFactory grainFactory)
        {
            // <set_request_context>
RequestContext.Set("trace-id", Guid.NewGuid().ToString("N"));

IOrderGrain order = grainFactory.GetGrain<IOrderGrain>("order-42");
await order.Submit();
            // </set_request_context>
        }

        internal static void ManageTenantContext()
        {
            // <manage_request_context>
object? value = RequestContext.Get("tenant-id");
RequestContext.Set("tenant-id", "tenant-17");
bool removed = RequestContext.Remove("tenant-id");
RequestContext.Clear();
            // </manage_request_context>
        }
    }

    // <read_request_context>
public sealed class OrderGrain(
    ILogger<OrderGrain> logger) : Grain, IOrderGrain
{
    public Task Submit()
    {
        string? traceId = RequestContext.Get("trace-id") as string;
        logger.LogInformation(
            "Submitting order with trace ID {TraceId}",
            traceId);

        return Task.CompletedTask;
    }
}
    // </read_request_context>
}

namespace Documentation.Grains.RequestScheduling
{
    public interface ICounterGrain : IGrainWithStringKey
    {
        ValueTask<int> AddAfterDelay(int amount);
    }

    // <serialized_counter_grain>
public sealed class CounterGrain : Grain, ICounterGrain
{
    private int _value;

    public async ValueTask<int> AddAfterDelay(int amount)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        _value += amount;
        return _value;
    }
}
    // </serialized_counter_grain>

    [GenerateSerializer]
    public sealed record Product([property: Id(0)] string Id);

    public interface ICatalogGrain : IGrainWithStringKey
    {
        ValueTask<Product> GetProduct(string productId);
    }

    // <reentrant_catalog_grain>
[Reentrant]
public sealed class CatalogGrain : Grain, ICatalogGrain
{
    public async ValueTask<Product> GetProduct(string productId)
    {
        return await LoadProduct(productId);
    }
}
    // </reentrant_catalog_grain>

    internal static class CatalogGrainHelpers
    {
        internal static ValueTask<Product> LoadProduct(string productId) =>
            ValueTask.FromResult(new Product(productId));
    }

    [GenerateSerializer]
    public sealed record Status([property: Id(0)] string Value);

    // <interleaving_method_attributes>
public interface IStatusGrain : IGrainWithStringKey
{
    Task Update(Status status);

    [ReadOnly]
    ValueTask<Status> Get();

    [AlwaysInterleave]
    ValueTask Ping();
}
    // </interleaving_method_attributes>

    [GenerateSerializer]
    public sealed record WorkItem(
        [property: Id(0)] bool IsReadOnly);

    public interface IWorkGrain : IGrainWithStringKey
    {
        Task Process(WorkItem item);
    }

    // <may_interleave_grain>
[MayInterleave(nameof(CanInterleave))]
public sealed class WorkGrain : Grain, IWorkGrain
{
    public static bool CanInterleave(IInvokable request)
    {
        return request.GetArgumentCount() == 1
            && request.GetArgument(0) is WorkItem { IsReadOnly: true };
    }

    public Task Process(WorkItem item) => Task.CompletedTask;
}
    // </may_interleave_grain>

    public interface IUserGrain : IGrainWithStringKey;

    public interface IChatRoomGrain : IGrainWithStringKey
    {
        Task Join(IUserGrain user);
    }

    internal sealed class UserGrain : Grain, IUserGrain
    {
        // <call_chain_reentrancy>
public async Task JoinRoom(string roomName)
{
    using var scope = RequestContext.AllowCallChainReentrancy();

    IChatRoomGrain room =
        GrainFactory.GetGrain<IChatRoomGrain>(roomName);

    await room.Join(this.AsReference<IUserGrain>());
}
        // </call_chain_reentrancy>
    }
}

namespace Documentation.Grains.RequestScheduling.BackgroundWork
{
    public interface IPriceFeed
    {
        IDisposable Subscribe(string symbol, Func<decimal, CancellationToken, Task> onPrice);
    }

    public interface IQuoteGrain : IGrainWithStringKey
    {
        ValueTask<decimal> GetPrice();
    }

    // <run_request_from_callback>
public sealed class QuoteGrain(IPriceFeed feed) : Grain, IQuoteGrain
{
    private IDisposable? _subscription;
    private decimal _price;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // The feed invokes OnPrice on its own threads.
        _subscription = feed.Subscribe(this.GetPrimaryKeyString(), OnPrice);
        return Task.CompletedTask;
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }

    public ValueTask<decimal> GetPrice() => ValueTask.FromResult(_price);

    // Runs the update as a request on this activation, where it can safely access grain state.
    private Task OnPrice(decimal price, CancellationToken cancellationToken) =>
        this.RunRequestAsync(
            static (state, _) =>
            {
                state.Grain._price = state.Price;
                return Task.CompletedTask;
            },
            (Grain: this, Price: price),
            cancellationToken);
}
    // </run_request_from_callback>

    public interface IConnectionMonitor
    {
        IDisposable Subscribe(string sessionId, Func<DateTimeOffset, CancellationToken, Task> onHeartbeat);
    }

    public interface ISessionGrain : IGrainWithStringKey
    {
        ValueTask<DateTimeOffset> GetLastSeen();
    }

    public sealed class SessionGrain(IConnectionMonitor monitor) : Grain, ISessionGrain
    {
        private IDisposable? _subscription;
        private DateTimeOffset _lastSeen;

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            _subscription = monitor.Subscribe(this.GetPrimaryKeyString(), OnHeartbeat);
            return Task.CompletedTask;
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            _subscription?.Dispose();
            return Task.CompletedTask;
        }

        public ValueTask<DateTimeOffset> GetLastSeen() => ValueTask.FromResult(_lastSeen);

        // <run_request_options>
// Heartbeats don't extend the activation's lifetime, and they run between the turns of other requests.
private Task OnHeartbeat(DateTimeOffset timestamp, CancellationToken cancellationToken) =>
    this.RunRequestAsync(
        static (state, _) =>
        {
            state.Grain._lastSeen = state.Timestamp;
            return Task.CompletedTask;
        },
        (Grain: this, Timestamp: timestamp),
        new GrainRequestOptions { KeepAlive = false, Interleave = true },
        cancellationToken);
        // </run_request_options>
    }

    [GenerateSerializer]
    public sealed record Order([property: Id(0)] string Id, [property: Id(1)] decimal Total);

    public interface IOrderBookGrain : IGrainWithStringKey
    {
        ValueTask<decimal> GetTotal();
    }

    public sealed class OrderBookGrain : Grain, IOrderBookGrain
    {
        private decimal _total;

        public ValueTask<decimal> GetTotal() => ValueTask.FromResult(_total);

        private int Apply(IReadOnlyList<Order> orders)
        {
            foreach (var order in orders)
            {
                _total += order.Total;
            }

            return orders.Count;
        }

        // <run_request_named>
// Call filters and tracing observe this request as IOrderFeed.ApplyOrders with one argument.
internal Task<int> ApplyOrders(IReadOnlyList<Order> orders, CancellationToken cancellationToken) =>
    this.RunRequestAsync(
        static (state, _) => Task.FromResult(state.Grain.Apply(state.Orders)),
        (Grain: this, Orders: orders),
        new GrainRequestOptions
        {
            InterfaceName = "IOrderFeed",
            MethodName = "ApplyOrders",
            Arguments = [orders.Count],
        },
        cancellationToken);
        // </run_request_named>
    }

    // <run_request_filter>
public sealed class RequestLoggingFilter(ILogger<RequestLoggingFilter> logger) : IIncomingGrainCallFilter
{
    public async Task Invoke(IIncomingGrainCallContext context)
    {
        logger.LogInformation(
            "Running {InterfaceName}.{MethodName} with {ArgumentCount} arguments",
            context.InterfaceName,
            context.MethodName,
            context.Request.GetArgumentCount());

        await context.Invoke();
    }
}
    // </run_request_filter>
}
