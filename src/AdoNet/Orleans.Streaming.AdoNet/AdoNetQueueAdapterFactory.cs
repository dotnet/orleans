using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Configuration.Overrides;
using Orleans.Streaming.AdoNet.Storage;

namespace Orleans.Streaming.AdoNet;

internal class AdoNetQueueAdapterFactory : IQueueAdapterFactory
{
    public AdoNetQueueAdapterFactory(string name, AdoNetStreamOptions streamOptions, ClusterOptions clusterOptions, SimpleQueueCacheOptions cacheOptions, HashRingStreamQueueMapperOptions hashOptions, ILoggerFactory loggerFactory, IServiceProvider serviceProvider)
    {
        _name = name;
        _streamOptions = streamOptions;
        _clusterOptions = clusterOptions;
        _serviceProvider = serviceProvider;

        _streamQueueMapper = new HashRingBasedStreamQueueMapper(hashOptions, name);
        _cache = new SimpleQueueAdapterCache(cacheOptions, name, loggerFactory);
        _adoNetQueueMapper = new AdoNetStreamQueueMapper(_streamQueueMapper);
        _adapterFactory = ActivatorUtilities.CreateFactory<AdoNetQueueAdapter>([typeof(string), typeof(AdoNetStreamOptions), typeof(ClusterOptions), typeof(IAdoNetStreamQueueMapper)]);
    }

    private readonly string _name;
    private readonly AdoNetStreamOptions _streamOptions;
    private readonly ClusterOptions _clusterOptions;
    private readonly IServiceProvider _serviceProvider;

    private readonly HashRingBasedStreamQueueMapper _streamQueueMapper;
    private readonly SimpleQueueAdapterCache _cache;
    private readonly IAdoNetStreamQueueMapper _adoNetQueueMapper;
    private readonly ObjectFactory<AdoNetQueueAdapter> _adapterFactory = ActivatorUtilities.CreateFactory<AdoNetQueueAdapter>([typeof(string), typeof(AdoNetStreamOptions), typeof(ClusterOptions), typeof(IAdoNetStreamQueueMapper), typeof(RelationalOrleansQueries)]);
    private readonly ObjectFactory<AdoNetStreamFailureHandler> _handlerFactory = ActivatorUtilities.CreateFactory<AdoNetStreamFailureHandler>([typeof(bool), typeof(AdoNetStreamOptions), typeof(ClusterOptions), typeof(IAdoNetStreamQueueMapper), typeof(RelationalOrleansQueries)]);

    private Task<RelationalOrleansQueries> GetQueriesAsync() => RelationalOrleansQueries.CreateInstance(_streamOptions.Invariant, _streamOptions.ConnectionString);

    public async Task<IQueueAdapter> CreateAdapter()
    {
        var queries = await GetQueriesAsync();

        return _adapterFactory(_serviceProvider, [_name, _streamOptions, _clusterOptions, _adoNetQueueMapper, queries]);
    }

    public async Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId)
    {
        var queries = await GetQueriesAsync();

        return _handlerFactory(_serviceProvider, [false, _streamOptions, _clusterOptions, _adoNetQueueMapper, queries]);
    }

    public IQueueAdapterCache GetQueueAdapterCache() => _cache;

    public IStreamQueueMapper GetStreamQueueMapper() => _streamQueueMapper;

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
        var hashOptions = serviceProvider.GetOptionsByName<HashRingStreamQueueMapperOptions>(name);

        return ActivatorUtilities.CreateInstance<AdoNetQueueAdapterFactory>(serviceProvider, [name, streamOptions, clusterOptions, cacheOptions, hashOptions]);
    }
}