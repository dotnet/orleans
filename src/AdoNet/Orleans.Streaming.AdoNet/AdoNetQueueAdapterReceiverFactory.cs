using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Streaming.AdoNet;

/// <summary>
/// Abstracts the creation of <see cref="AdoNetQueueAdapterReceiver"/> instances.
/// </summary>
internal interface IAdoNetQueueAdapterReceiverFactory
{
    IQueueAdapterReceiver Create(string providerId, string queueId, AdoNetStreamOptions adoNetStreamingOptions);
}

/// <summary>
/// Creates <see cref="AdoNetQueueAdapterReceiver"/> instances.
/// </summary>
internal class AdoNetQueueAdapterReceiverFactory(IServiceProvider serviceProvider) : IAdoNetQueueAdapterReceiverFactory
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    public IQueueAdapterReceiver Create(string providerId, string queueId, AdoNetStreamOptions adoNetStreamingOptions)
    {
        var clusterOptions = _serviceProvider.GetRequiredService<IOptions<ClusterOptions>>();
        var serializer = _serviceProvider.GetRequiredService<Serializer<AdoNetBatchContainer>>();
        var logger = _serviceProvider.GetRequiredService<ILogger<AdoNetQueueAdapterReceiver>>();

        return new AdoNetQueueAdapterReceiver(providerId, queueId, adoNetStreamingOptions, clusterOptions, serializer, logger);
    }
}