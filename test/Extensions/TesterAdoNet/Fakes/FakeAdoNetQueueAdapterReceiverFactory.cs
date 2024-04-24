using Orleans.Configuration;
using Orleans.Streaming.AdoNet;
using Orleans.Streams;

namespace Tester.AdoNet.Fakes;

internal class FakeAdoNetQueueAdapterReceiverFactory
    (Func<string, string, AdoNetStreamingOptions, IQueueAdapterReceiver> create = null) : IAdoNetQueueAdapterReceiverFactory
{
    public IQueueAdapterReceiver Create(string providerId, string queueId, AdoNetStreamingOptions adoNetStreamingOptions) =>
        create is not null ? create(providerId, queueId, adoNetStreamingOptions) : new FakeAdoNetQueueAdapterReceiver(providerId, queueId, adoNetStreamingOptions);
}