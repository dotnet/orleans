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
public interface IProductRecommendations
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
    IProductRecommendations recommendations)
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
