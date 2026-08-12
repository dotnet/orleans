using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Serialization;

namespace Documentation.Grains.Overview
{
    // <grain_interface>
public interface IShoppingCartGrain : IGrainWithStringKey
{
    ValueTask AddItem(CartItem item);

    ValueTask<IReadOnlyList<CartItem>> GetItems();

    Task Checkout();

    Task<Receipt> GetReceipt();
}

[GenerateSerializer]
public sealed record CartItem(
    [Id(0)] string ProductId,
    [Id(1)] int Quantity);

[GenerateSerializer]
public sealed record Receipt(
    [Id(0)] string OrderId);
    // </grain_interface>

    // <grain_implementation>
public sealed class ShoppingCartGrain : Grain, IShoppingCartGrain
{
    private readonly List<CartItem> _items = [];

    public ValueTask AddItem(CartItem item)
    {
        _items.Add(item);
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<CartItem>> GetItems() =>
        ValueTask.FromResult<IReadOnlyList<CartItem>>(_items.ToArray());

    public Task Checkout() => Task.CompletedTask;

    public Task<Receipt> GetReceipt() =>
        Task.FromResult(new Receipt($"order-{this.GetPrimaryKeyString()}"));
}
    // </grain_implementation>

    internal static class GrainCalls
    {
        internal static async Task UseShoppingCart(IGrainFactory grainFactory)
        {
            // <get_grain>
IShoppingCartGrain cart =
    grainFactory.GetGrain<IShoppingCartGrain>("customer-42");

await cart.AddItem(new CartItem("SKU-123", 2));
IReadOnlyList<CartItem> items = await cart.GetItems();
            // </get_grain>
        }
    }

    public sealed class Report;

    // <response_timeout>
public interface IReportGrain : IGrainWithGuidKey
{
    [ResponseTimeout("00:00:10")]
    Task<Report> Generate(CancellationToken cancellationToken = default);
}
    // </response_timeout>

    internal sealed class LifecycleGrain : Grain
    {
        // <activation_overrides>
public override Task OnActivateAsync(CancellationToken cancellationToken)
{
    return base.OnActivateAsync(cancellationToken);
}

public override Task OnDeactivateAsync(
    DeactivationReason reason,
    CancellationToken cancellationToken)
{
    return base.OnDeactivateAsync(reason, cancellationToken);
}
        // </activation_overrides>
    }
}

namespace Documentation.Grains.Cancellation
{
    // <cancellable_grain_interface>
public interface IImportGrain : IGrainWithGuidKey
{
    Task<int> Import(
        IReadOnlyList<string> records,
        CancellationToken cancellationToken = default);
}
    // </cancellable_grain_interface>

    // <observe_cancellation>
public sealed class ImportGrain : Grain, IImportGrain
{
    public async Task<int> Import(
        IReadOnlyList<string> records,
        CancellationToken cancellationToken = default)
    {
        var imported = 0;

        foreach (string record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SaveRecord(record, cancellationToken);
            imported++;
        }

        return imported;
    }

    private static Task SaveRecord(
        string record,
        CancellationToken cancellationToken) =>
        Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
}
    // </observe_cancellation>

    internal static class Caller
    {
        internal static async Task Import(
            IGrainFactory grainFactory,
            IReadOnlyList<string> records)
        {
            // <cancel_grain_call>
IImportGrain importer =
    grainFactory.GetGrain<IImportGrain>(Guid.NewGuid());

using var cancellation = new CancellationTokenSource(
    TimeSpan.FromSeconds(30));

try
{
    await importer.Import(records, cancellation.Token);
}
catch (OperationCanceledException)
    when (cancellation.IsCancellationRequested)
{
    // The caller requested cancellation.
}
            // </cancel_grain_call>
        }

        internal static void ConfigureTimeoutCancellation(
            ISiloBuilder siloBuilder,
            IClientBuilder clientBuilder)
        {
            // <cancel_request_on_timeout>
siloBuilder.Configure<SiloMessagingOptions>(options =>
{
    options.CancelRequestOnTimeout = true;
});

clientBuilder.Configure<ClientMessagingOptions>(options =>
{
    options.CancelRequestOnTimeout = true;
});
            // </cancel_request_on_timeout>
        }
    }
}

namespace Documentation.Grains.Identity
{
    // <string_key_interface>
public interface IDeviceGrain : IGrainWithStringKey
{
    ValueTask<string> GetStatus();
}
    // </string_key_interface>

    internal static class Caller
    {
        internal static void GetDevice(IGrainFactory grainFactory)
        {
            // <get_string_key_grain>
IDeviceGrain device = grainFactory.GetGrain<IDeviceGrain>("device-17");
            // </get_string_key_grain>
        }

        internal static void CreateIdentity()
        {
            // <create_grain_id>
GrainId grainId = GrainId.Create(
    GrainType.Create("shopping-cart"),
    IdSpan.Create("customer-42"));
            // </create_grain_id>
        }
    }

    // <read_string_key>
public sealed class DeviceGrain : Grain, IDeviceGrain
{
    public ValueTask<string> GetStatus()
    {
        string deviceId = this.GetPrimaryKeyString();
        return ValueTask.FromResult($"Device {deviceId} is online");
    }
}
    // </read_string_key>

    public interface IShoppingCartGrain : IGrainWithStringKey;

    // <stable_grain_type>
[GrainType("shopping-cart")]
public sealed class ShoppingCartGrain : Grain, IShoppingCartGrain
{
}
    // </stable_grain_type>
}

namespace Documentation.Grains.Lifecycle
{
    public interface IDeviceGrain : IGrainWithStringKey;

    public interface IDeviceConnection : IAsyncDisposable;

    public interface IDeviceConnectionFactory
    {
        Task<IDeviceConnection> ConnectAsync(
            string deviceId,
            CancellationToken cancellationToken);
    }

    // <activate_grain>
public sealed class DeviceGrain(
    IDeviceConnectionFactory connectionFactory) : Grain, IDeviceGrain
{
    private IDeviceConnection? _connection;

    public override async Task OnActivateAsync(
        CancellationToken cancellationToken)
    {
        _connection = await connectionFactory.ConnectAsync(
            this.GetPrimaryKeyString(),
            cancellationToken);

        await base.OnActivateAsync(cancellationToken);
    }
}
    // </activate_grain>

    internal sealed class DeactivationGrain : Grain
    {
        private IDeviceConnection? _connection = null;

    // <deactivate_grain>
public override async Task OnDeactivateAsync(
    DeactivationReason reason,
    CancellationToken cancellationToken)
{
    if (_connection is not null)
    {
        await _connection.DisposeAsync();
    }

    await base.OnDeactivateAsync(reason, cancellationToken);
}
    // </deactivate_grain>

    // <deactivate_on_idle>
public Task Close()
{
    DeactivateOnIdle();
    return Task.CompletedTask;
}
    // </deactivate_on_idle>
    }

    // <lifecycle_participant>
public sealed class CacheParticipant : ILifecycleParticipant<IGrainLifecycle>
{
    public void Participate(IGrainLifecycle lifecycle)
    {
        lifecycle.Subscribe<CacheParticipant>(
            GrainLifecycleStage.Activate,
            OnStart,
            OnStop);
    }

    private Task OnStart(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    private Task OnStop(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
    // </lifecycle_participant>

    internal sealed class MigratingGrain : Grain
    {
        // <migrate_on_idle>
public Task RequestMigration()
{
    MigrateOnIdle();
    return Task.CompletedTask;
}
        // </migrate_on_idle>
    }

    public interface ISessionGrain : IGrainWithStringKey;

    // <migration_participant>
public sealed class SessionGrain :
    Grain,
    ISessionGrain,
    IGrainMigrationParticipant
{
    private int _sequence;

    public void OnDehydrate(IDehydrationContext context)
    {
        context.TryAddValue("sequence", _sequence);
    }

    public void OnRehydrate(IRehydrationContext context)
    {
        context.TryGetValue("sequence", out _sequence);
    }
}
    // </migration_participant>
}

namespace Documentation.Grains.References.Getting
{
    // <counter_interface>
public interface ICounterGrain : IGrainWithStringKey
{
    ValueTask<int> Add(int amount);
}
    // </counter_interface>

    internal static class Caller
    {
        internal static async Task Add(IGrainFactory grainFactory)
        {
            // <get_counter_reference>
ICounterGrain counter =
    grainFactory.GetGrain<ICounterGrain>("orders-processed");

int value = await counter.Add(1);
            // </get_counter_reference>
        }
    }
}

namespace Documentation.Grains.References.Resolution
{
    // <marker_interfaces>
public interface ICounterGrain : IGrainWithStringKey
{
    ValueTask<int> Add(int amount);
}

public interface IUpCounterGrain : ICounterGrain;

public interface IDownCounterGrain : ICounterGrain;

public sealed class UpCounterGrain : Grain, IUpCounterGrain
{
    private int _value;

    public ValueTask<int> Add(int amount) =>
        ValueTask.FromResult(_value += amount);
}

public sealed class DownCounterGrain : Grain, IDownCounterGrain
{
    private int _value;

    public ValueTask<int> Add(int amount) =>
        ValueTask.FromResult(_value -= amount);
}
    // </marker_interfaces>

    internal static class Caller
    {
        internal static void GetCounters(IGrainFactory grainFactory)
        {
            // <get_marker_references>
IUpCounterGrain up =
    grainFactory.GetGrain<IUpCounterGrain>("counter");

IDownCounterGrain down =
    grainFactory.GetGrain<IDownCounterGrain>("counter");
            // </get_marker_references>
        }
    }
}

namespace Documentation.Grains.References.Casting
{
    public interface IUserGrain : IGrainWithStringKey;

    public interface IUserProfileGrain : IGrainWithStringKey;

    internal static class Caller
    {
        internal static void GetProfile(IGrainFactory grainFactory)
        {
            // <cast_grain_reference>
IUserGrain user = grainFactory.GetGrain<IUserGrain>("user-42");
IUserProfileGrain profile = user.AsReference<IUserProfileGrain>();
            // </cast_grain_reference>
        }
    }

    internal sealed class UserGrain : Grain, IUserGrain, IUserProfileGrain
    {
        internal void PassSelf()
        {
            // <self_reference>
IUserGrain self = this.AsReference<IUserGrain>();
            // </self_reference>
        }
    }
}
