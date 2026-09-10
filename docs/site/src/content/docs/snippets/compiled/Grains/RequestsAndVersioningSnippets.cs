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
            static (state, _) => state.Grain._price = state.Price,
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
        static (state, _) => state.Grain._lastSeen = state.Timestamp,
        (Grain: this, Timestamp: timestamp),
        new LocalRequestOptions { KeepAlive = false, Interleave = true },
        cancellationToken);
        // </run_request_options>
    }

    public interface IIngestGrain : IGrainWithStringKey
    {
        ValueTask<int> GetCount();
    }

    public sealed class IngestGrain : Grain, IIngestGrain
    {
        private int _count;

        public ValueTask<int> GetCount() => ValueTask.FromResult(_count);

        internal void Increment() => _count++;
    }

    // <receive_local_request>
// A request which increments IngestGrain's counter. Call filters observe it as IIngestRequests.Increment.
internal sealed class IncrementRequest : Request, ILocalRequest
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IngestGrain? _grain;

    public Task Completion => _completion.Task;

    // Receives the outcome once the request and its call filters complete, or when the activation rejects the request.
    // The task keeps the first outcome.
    public void Complete(Response response)
    {
        if (response.Exception is { } exception)
        {
            _completion.TrySetException(exception);
        }
        else
        {
            _completion.TrySetResult();
        }
    }

    public override object GetTarget() => _grain!;

    public override void SetTarget(ITargetHolder holder) => _grain = (IngestGrain?)holder.GetTarget();

    protected override ValueTask InvokeInner()
    {
        _grain!.Increment();
        return default;
    }

    public override string GetInterfaceName() => "IIngestRequests";

    public override string GetMethodName() => "Increment";

    public override string GetActivityName() => "IIngestRequests/Increment";

    public override Type GetInterfaceType() => typeof(IIngestGrain);

    public override System.Reflection.MethodInfo GetMethod() => typeof(IIngestGrain).GetMethod(nameof(IIngestGrain.GetCount))!;

    public override void Dispose() { }
}

public static class IngestRequests
{
    // Delivers the request to the activation and returns a task which completes with the request's outcome.
    public static Task Increment(IGrainContext activation)
    {
        var request = new IncrementRequest();
        activation.ReceiveLocalRequest(request, default);
        return request.Completion;
    }
}
    // </receive_local_request>
}

namespace Documentation.Grains.ReadScaling
{
    [GenerateSerializer]
    public sealed record ProductSnapshot(
        [property: Id(0)] string Name,
        [property: Id(1)] decimal Price,
        [property: Id(2)] long Version);

    [GenerateSerializer]
    public sealed record ProductView(
        [property: Id(0)] ProductSnapshot Product,
        [property: Id(1)] string Recommendation);

    // <single_writer_interleaved_readers>
public interface IProductRecommendationService
{
    ValueTask<string> GetRecommendation(string productId);
}

public interface IProductGrain : IGrainWithStringKey
{
    [ReadOnly]
    ValueTask<ProductView> Get();

    ValueTask Update(string name, decimal price);
}

public sealed class ProductGrain(
    IProductRecommendationService recommendations)
    : Grain, IProductGrain
{
    private ProductSnapshot _snapshot =
        new("Product", 0m, Version: 0);

    public async ValueTask<ProductView> Get()
    {
        ProductSnapshot snapshot = _snapshot;
        string recommendation = await recommendations.GetRecommendation(
            this.GetPrimaryKeyString());

        return new ProductView(snapshot, recommendation);
    }

    public ValueTask Update(string name, decimal price)
    {
        _snapshot = new ProductSnapshot(
            name,
            price,
            _snapshot.Version + 1);

        return ValueTask.CompletedTask;
    }
}
    // </single_writer_interleaved_readers>
}
