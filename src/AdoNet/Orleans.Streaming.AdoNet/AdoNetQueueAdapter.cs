using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Streaming.AdoNet.Storage;

namespace Orleans.Streaming.AdoNet;

/// <summary>
/// Stream queue storage adapter for ADO.NET providers.
/// </summary>
internal class AdoNetQueueAdapter(string name, ILogger<AdoNetQueueAdapter> logger, AdoNetStreamOptions streamOptions, ClusterOptions clusterOptions, IConsistentRingStreamQueueMapper mapper, Serializer<AdoNetBatchContainer> serializer, IServiceProvider serviceProvider) : IQueueAdapter
{
    private readonly ILogger<AdoNetQueueAdapter> _logger = logger;
    private readonly AdoNetStreamOptions _streamOptions = streamOptions;
    private readonly ClusterOptions _clusterOptions = clusterOptions;
    private readonly IConsistentRingStreamQueueMapper _mapper = mapper;
    private readonly Serializer<AdoNetBatchContainer> _serializer = serializer;
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    /// <summary>
    /// Caches queue names to avoid garbage allocations.
    /// </summary>
    private readonly ConcurrentDictionary<QueueId, string> _queues = new();

    /// <summary>
    /// Caches the queue name creation delegate.
    /// </summary>
    private readonly Func<QueueId, string> _getQueueName = (QueueId queueId) => queueId.ToString();

    /// <summary>
    /// The adonet repository abstraction.
    /// </summary>
    private readonly Lazy<Task<RelationalOrleansQueries>> _queries = new(() => RelationalOrleansQueries.CreateInstance(streamOptions.Invariant, streamOptions.ConnectionString), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The receiver factory.
    /// </summary>
    private readonly ObjectFactory<AdoNetQueueAdapterReceiver> _receiverFactory = ActivatorUtilities.CreateFactory<AdoNetQueueAdapterReceiver>([typeof(string), typeof(string), typeof(string), typeof(AdoNetStreamOptions)]);

    /// <summary>
    /// Maps to the ProviderId in the database.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// The ADO.NET provider is not yet rewindable.
    /// </summary>
    public bool IsRewindable => false;

    /// <summary>
    /// The ADO.NET provider works both ways.
    /// </summary>
    public StreamProviderDirection Direction => StreamProviderDirection.ReadWrite;

    public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
    {
        // get the adonet queue id
        var adoNetQueueId = GetAdoNetQueueId(queueId);

        // create the receiver
        return _receiverFactory(_serviceProvider, [_clusterOptions.ServiceId, Name, adoNetQueueId, _streamOptions]);
    }

    public async Task QueueMessageBatchAsync<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
    {
        // the adonet provider is not rewindable so we do not support user supplied tokens
        if (token is not null)
        {
            throw new ArgumentException($"{nameof(AdoNetQueueAdapter)} does yet support a user supplied {nameof(StreamSequenceToken)}.");
        }

        // map the orleans stream id to the corresponding queue id
        var queueId = _mapper.GetQueueForStream(streamId);

        // get the adonet queue id
        var adoNetQueueId = GetAdoNetQueueId(queueId);

        // create the payload from the events
        var container = new AdoNetBatchContainer(streamId, events.Cast<object>().ToList(), requestContext);
        var payload = _serializer.SerializeToArray(container);

        // we can enqueue the message now
        var queries = await _queries.Value;
        await queries.QueueMessageBatchAsync(_clusterOptions.ServiceId, Name, adoNetQueueId, payload, _streamOptions.ExpiryTimeout);
    }

    private string GetAdoNetQueueId(QueueId queueId) => _queues.GetOrAdd(queueId, _getQueueName);
}