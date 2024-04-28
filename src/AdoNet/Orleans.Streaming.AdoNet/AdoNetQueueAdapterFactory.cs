namespace Orleans.Streaming.AdoNet;

internal class AdoNetQueueAdapterFactory : IQueueAdapterFactory
{
    public AdoNetQueueAdapterFactory(string name, AdoNetStreamOptions streamOptions, ClusterOptions clusterOptions, SimpleQueueCacheOptions cacheOptions, HashRingStreamQueueMapperOptions hashOptions, StreamPullingAgentOptions agentOptions, ILoggerFactory loggerFactory, IServiceProvider serviceProvider)
    {
        _name = name;
        _streamOptions = streamOptions;
        _clusterOptions = clusterOptions;
        _agentOptions = agentOptions;
        _serviceProvider = serviceProvider;

        _streamQueueMapper = new HashRingBasedStreamQueueMapper(hashOptions, name);
        _cache = new SimpleQueueAdapterCache(cacheOptions, name, loggerFactory);
        _adoNetQueueMapper = new AdoNetStreamQueueMapper(_streamQueueMapper);
    }

    private readonly string _name;
    private readonly AdoNetStreamOptions _streamOptions;
    private readonly ClusterOptions _clusterOptions;
    private readonly StreamPullingAgentOptions _agentOptions;
    private readonly IServiceProvider _serviceProvider;

    private readonly HashRingBasedStreamQueueMapper _streamQueueMapper;
    private readonly SimpleQueueAdapterCache _cache;
    private readonly AdoNetStreamQueueMapper _adoNetQueueMapper;

    private Task<RelationalOrleansQueries> GetQueriesAsync() => RelationalOrleansQueries.CreateInstance(_streamOptions.Invariant, _streamOptions.ConnectionString);

    public async Task<IQueueAdapter> CreateAdapter()
    {
        var queries = await GetQueriesAsync();

        return AdapterFactory(_serviceProvider, [_name, _streamOptions, _clusterOptions, _adoNetQueueMapper, _agentOptions, queries]);
    }

    public async Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId)
    {
        var queries = await GetQueriesAsync();

        return HandlerFactory(_serviceProvider, [false, _streamOptions, _clusterOptions, _adoNetQueueMapper, queries]);
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
        var agentOptions = serviceProvider.GetOptionsByName<StreamPullingAgentOptions>(name);

        return QueueAdapterFactoryFactory(serviceProvider, [name, streamOptions, clusterOptions, cacheOptions, hashOptions, agentOptions]);
    }

    /// <summary>
    /// Factory of <see cref="AdoNetQueueAdapterFactory"/> instances.
    /// </summary>
    private static readonly ObjectFactory<AdoNetQueueAdapterFactory> QueueAdapterFactoryFactory = ActivatorUtilities.CreateFactory<AdoNetQueueAdapterFactory>([typeof(string), typeof(AdoNetStreamOptions), typeof(ClusterOptions), typeof(SimpleQueueCacheOptions), typeof(HashRingStreamQueueMapperOptions), typeof(StreamPullingAgentOptions)]);

    /// <summary>
    /// Factory of <see cref="AdoNetQueueAdapter"/> instances.
    /// </summary>
    private static readonly ObjectFactory<AdoNetQueueAdapter> AdapterFactory = ActivatorUtilities.CreateFactory<AdoNetQueueAdapter>([typeof(string), typeof(AdoNetStreamOptions), typeof(ClusterOptions), typeof(AdoNetStreamQueueMapper), typeof(StreamPullingAgentOptions), typeof(RelationalOrleansQueries)]);

    /// <summary>
    /// Factory of <see cref="AdoNetStreamFailureHandler"/> instances.
    /// </summary>
    private static readonly ObjectFactory<AdoNetStreamFailureHandler> HandlerFactory = ActivatorUtilities.CreateFactory<AdoNetStreamFailureHandler>([typeof(bool), typeof(AdoNetStreamOptions), typeof(ClusterOptions), typeof(AdoNetStreamQueueMapper), typeof(RelationalOrleansQueries)]);
}