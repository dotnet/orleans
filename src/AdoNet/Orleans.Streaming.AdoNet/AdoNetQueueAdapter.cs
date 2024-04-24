using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Streaming.AdoNet.Storage;

namespace Orleans.Streaming.AdoNet;

/// <summary>
/// Stream queue storage adapter for ADO.NET providers.
/// </summary>
internal class AdoNetQueueAdapter(string providerId, AdoNetStreamingOptions adoNetStreamingOptions, ILogger<AdoNetQueueAdapter> logger, IConsistentRingStreamQueueMapper mapper, Serializer<AdoNetBatchContainer> serializer, IAdoNetQueueAdapterReceiverFactory receiverFactory, IOptions<ClusterOptions> clusterOptions) : IQueueAdapter
{
    private readonly ILogger<AdoNetQueueAdapter> _logger = logger;
    private readonly AdoNetStreamingOptions _adoNetStreamingOptions = adoNetStreamingOptions;
    private readonly IConsistentRingStreamQueueMapper _mapper = mapper;
    private readonly Serializer<AdoNetBatchContainer> _serializer = serializer;
    private readonly IAdoNetQueueAdapterReceiverFactory _receiverFactory = receiverFactory;
    private readonly IOptions<ClusterOptions> _clusterOptions = clusterOptions;

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
    private readonly Lazy<Task<RelationalOrleansQueries>> _queries = new(() => RelationalOrleansQueries.CreateInstance(adoNetStreamingOptions.Invariant, adoNetStreamingOptions.ConnectionString), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Maps to the ProviderId in the database.
    /// </summary>
    public string Name { get; } = providerId;

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
        return _receiverFactory.Create(Name, adoNetQueueId, _adoNetStreamingOptions);
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
        await queries.QueueMessageBatchAsync(_clusterOptions.Value.ServiceId, Name, adoNetQueueId, payload, _adoNetStreamingOptions.ExpiryTimeout);
    }

    private string GetAdoNetQueueId(QueueId queueId) => _queues.GetOrAdd(queueId, _getQueueName);
}