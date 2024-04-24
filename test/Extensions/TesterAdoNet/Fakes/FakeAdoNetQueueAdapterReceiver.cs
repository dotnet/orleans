using Orleans.Configuration;
using Orleans.Streams;

namespace Tester.AdoNet.Fakes;

internal class FakeAdoNetQueueAdapterReceiver(string providerId, string queueId, AdoNetStreamOptions adoNetStreamingOptions) : IQueueAdapterReceiver
{
    public string ProviderId { get; } = providerId;
    public string QueueId { get; } = queueId;
    public AdoNetStreamOptions AdoNetStreamingOptions { get; } = adoNetStreamingOptions;

    public Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount) => Task.FromResult<IList<IBatchContainer>>([]);

    public Task Initialize(TimeSpan timeout) => Task.CompletedTask;

    public Task MessagesDeliveredAsync(IList<IBatchContainer> messages) => Task.CompletedTask;

    public Task Shutdown(TimeSpan timeout) => Task.CompletedTask;
}