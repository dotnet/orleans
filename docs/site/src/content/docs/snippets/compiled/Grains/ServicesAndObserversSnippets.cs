using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Concurrency;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.Services;
using Orleans.Serialization;
using Orleans.Services;
using Orleans.Utilities;

namespace Documentation.Grains.Services
{
    // <index_grain_service_interface>
public interface IIndexService : IGrainService
{
    Task Add(string key);
}
    // </index_grain_service_interface>

    // <index_grain_service>
[Reentrant]
public sealed class IndexService :
    GrainService,
    IIndexService
{
    public IndexService(
        GrainId id,
        Silo silo,
        ILoggerFactory loggerFactory)
        : base(id, silo, loggerFactory)
    {
    }

    public Task Add(string key)
    {
        return Task.CompletedTask;
    }
}
    // </index_grain_service>

    // <index_grain_service_client>
public interface IIndexServiceClient :
    IGrainServiceClient<IIndexService>
{
    Task Add(string key);
}

public sealed class IndexServiceClient :
    GrainServiceClient<IIndexService>,
    IIndexServiceClient
{
    public IndexServiceClient(IServiceProvider services)
        : base(services)
    {
    }

    public Task Add(string key)
    {
        var grainReference = CurrentGrainReference
            ?? throw new InvalidOperationException(
                "Grain service clients can only be called from a grain.");
        IIndexService service =
            GetGrainService(grainReference.GrainId);

        return service.Add(key);
    }
}
    // </index_grain_service_client>

    internal static class GrainServiceConfiguration
    {
        internal static void Configure(ISiloBuilder siloBuilder)
        {
            // <register_index_grain_service>
siloBuilder.AddGrainService<IndexService>();
siloBuilder.Services.AddSingleton<
    IIndexServiceClient,
    IndexServiceClient>();
            // </register_index_grain_service>
        }
    }

    public interface IDocumentGrain : IGrainWithStringKey
    {
        Task Index();
    }

    // <use_index_grain_service>
public sealed class DocumentGrain(
    IIndexServiceClient indexService) :
    Grain,
    IDocumentGrain
{
    public Task Index()
    {
        return indexService.Add(this.GetPrimaryKeyString());
    }
}
    // </use_index_grain_service>
}

namespace Documentation.Grains.Observers
{
    // <chat_observer>
public interface IChatObserver : IGrainObserver
{
    Task ReceiveMessage(string room, string message);
}

public sealed class ChatObserver : IChatObserver
{
    public Task ReceiveMessage(string room, string message)
    {
        Console.WriteLine($"[{room}] {message}");
        return Task.CompletedTask;
    }
}
    // </chat_observer>

    public interface IChatRoomGrain : IGrainWithStringKey
    {
        Task Subscribe(IChatObserver observer);

        Task Unsubscribe(IChatObserver observer);

        Task Publish(string message);
    }

    internal static class ObserverClient
    {
        internal static async Task Subscribe(IGrainFactory grainFactory)
        {
            // <subscribe_observer>
var observer = new ChatObserver();
IChatObserver observerReference =
    grainFactory.CreateObjectReference<IChatObserver>(observer);

IChatRoomGrain room =
    grainFactory.GetGrain<IChatRoomGrain>("general");

await room.Subscribe(observerReference);
            // </subscribe_observer>

            // <unsubscribe_observer>
await room.Unsubscribe(observerReference);
grainFactory.DeleteObjectReference<IChatObserver>(observerReference);
            // </unsubscribe_observer>
        }
    }

    // <chat_room_observer_manager>
public sealed class ChatRoomGrain : Grain, IChatRoomGrain
{
    private readonly ObserverManager<IChatObserver> _observers;

    public ChatRoomGrain(ILogger<ChatRoomGrain> logger)
    {
        _observers = new(
            TimeSpan.FromMinutes(5),
            logger);
    }

    public Task Subscribe(IChatObserver observer)
    {
        _observers.Subscribe(observer, observer);
        return Task.CompletedTask;
    }

    public Task Unsubscribe(IChatObserver observer)
    {
        _observers.Unsubscribe(observer);
        return Task.CompletedTask;
    }

    public Task Publish(string message)
    {
        return _observers.Notify(
            observer => observer.ReceiveMessage(
                this.GetPrimaryKeyString(),
                message));
    }
}
    // </chat_room_observer_manager>

    public interface IUserGrain : IGrainWithStringKey;

    internal sealed class UserGrain : Grain, IUserGrain, IChatObserver
    {
        public Task ReceiveMessage(string room, string message) =>
            Task.CompletedTask;

        internal async Task Subscribe(IChatRoomGrain room)
        {
            // <subscribe_grain_observer>
IChatObserver observer =
    this.AsReference<IChatObserver>();

await room.Subscribe(observer);
            // </subscribe_grain_observer>
        }
    }
}

namespace Documentation.Grains.OneWay
{
    [GenerateSerializer]
    public sealed record AuditEntry([property: Id(0)] string Message);

    // <one_way_audit>
public interface IAuditGrain : IGrainWithStringKey
{
    [OneWay]
    ValueTask Record(AuditEntry entry);
}
    // </one_way_audit>
}
