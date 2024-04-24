using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Configuration.Overrides;

namespace Orleans.Streaming.AdoNet;

internal class AdoNetQueueAdapterFactory(string name, AdoNetStreamOptions streamOptions, ClusterOptions clusterOptions, SimpleQueueCacheOptions cacheOptions, HashRingStreamQueueMapperOptions mapperOptions, Serializer<AdoNetBatchContainer> serializer, ILoggerFactory loggerFactory) : IQueueAdapterFactory
{
    private readonly string _name = name;
    private readonly AdoNetStreamOptions _streamOptions = streamOptions;
    private readonly ClusterOptions _clusterOptions = clusterOptions;
    private readonly Serializer<AdoNetBatchContainer> _serializer = serializer;
    private readonly HashRingBasedStreamQueueMapper _mapper = new(mapperOptions, name);
    private readonly SimpleQueueAdapterCache _cache = new(cacheOptions, name, loggerFactory);
    private readonly ILoggerFactory _loggerFactory = loggerFactory;

    public Task<IQueueAdapter> CreateAdapter() => Task.FromResult<IQueueAdapter>(new AdoNetQueueAdapter(_name, _loggerFactory.CreateLogger<AdoNetQueueAdapter>(), _streamOptions, _clusterOptions, _mapper, _serializer, null));

    public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId) => throw new NotImplementedException();

    public IQueueAdapterCache GetQueueAdapterCache() => _cache;

    public IStreamQueueMapper GetStreamQueueMapper() => _mapper;

    /// <summary>
    /// Used by the silo and client configurators as an entry point to set up a stream.
    /// </summary>
    public static IQueueAdapterFactory Create(IServiceProvider serviceProvider, string name)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(name);

        var streamOptions = serviceProvider.GetOptionsByName<AdoNetStreamOptions>(name);
        var clusterOptions = serviceProvider.GetProviderClusterOptions(name).Value;
        var cacheOptions = serviceProvider.GetOptionsByName<SimpleQueueCacheOptions>(name);
        var mapperOptions = serviceProvider.GetOptionsByName<HashRingStreamQueueMapperOptions>(name);

        return ActivatorUtilities.CreateInstance<AdoNetQueueAdapterFactory>(serviceProvider, [streamOptions, clusterOptions, cacheOptions, mapperOptions]);
    }
}