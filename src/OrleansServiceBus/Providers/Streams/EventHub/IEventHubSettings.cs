
namespace OrleansServiceBusUtils.Providers.Streams.EventHub
{
    public interface IEventHubSettings
    {
        string ConnectionString { get; }
        string ConsumerGroup { get; }
        string Path { get; }
        int? PrefetchCount { get; }
    }
}
