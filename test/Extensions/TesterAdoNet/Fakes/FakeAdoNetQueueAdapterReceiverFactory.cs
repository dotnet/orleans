using Orleans.Configuration;
using Orleans.Streaming.AdoNet;
using Orleans.Streams;

namespace Tester.AdoNet.Fakes;

internal class FakeAdoNetQueueAdapterReceiverFactory
    (Func<string, string, AdoNetStreamOptions, IQueueAdapterReceiver> create = null) : IAdoNetQueueAdapterReceiverFactory
{
    public IQueueAdapterReceiver Create(string providerId, string queueId, AdoNetStreamOptions adoNetStreamingOptions) =>
        create is not null ? create(providerId, queueId, adoNetStreamingOptions) : new FakeAdoNetQueueAdapterReceiver(providerId, queueId, adoNetStreamingOptions);
}