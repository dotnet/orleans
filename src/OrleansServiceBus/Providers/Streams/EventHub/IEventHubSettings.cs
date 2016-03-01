

namespace Orleans.ServiceBus.Providers
{
    public interface IEventHubSettings
    {
        string ConnectionString { get; }
        string ConsumerGroup { get; }
        string Path { get; }
        int? PrefetchCount { get; }
        bool StartFromNow { get; }
    }
}
