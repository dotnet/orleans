using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Streaming.AdoNet.Storage;

namespace Orleans.Streaming.AdoNet;

/// <summary>
/// Stream queue storage adapter for ADO.NET providers.
/// </summary>
internal partial class AdoNetQueueAdapter(string name, AdoNetStreamOptions streamOptions, ClusterOptions clusterOptions, AdoNetStreamQueueMapper mapper, RelationalOrleansQueries queries, Serializer<AdoNetBatchContainer> serializer, ILogger<AdoNetQueueAdapter> logger, IServiceProvider serviceProvider) : IQueueAdapter
{
    private readonly ILogger<AdoNetQueueAdapter> _logger = logger;

    /// <summary>
    /// The receiver factory.
    /// </summary>
    private readonly ObjectFactory<AdoNetQueueAdapterReceiver> _receiverFactory = ActivatorUtilities.CreateFactory<AdoNetQueueAdapterReceiver>([typeof(string), typeof(string), typeof(AdoNetStreamOptions), typeof(ClusterOptions), typeof(RelationalOrleansQueries)]);

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
        // map the queue id
        var adoNetQueueId = mapper.GetAdoNetQueueId(queueId);

        // create the receiver
        return _receiverFactory(serviceProvider, [Name, adoNetQueueId, streamOptions, clusterOptions, queries]);
    }

    public async Task QueueMessageBatchAsync<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
    {
        // the adonet provider is not rewindable so we do not support user supplied tokens
        if (token is not null)
        {
            throw new ArgumentException($"{nameof(AdoNetQueueAdapter)} does yet support a user supplied {nameof(StreamSequenceToken)}.");
        }

        // map the orleans stream id to the corresponding queue id
        var queueId = mapper.GetAdoNetQueueId(streamId);

        // create the payload from the events
        var container = new AdoNetBatchContainer(streamId, events.Cast<object>().ToList(), requestContext);
        var payload = serializer.SerializeToArray(container);

        // we can enqueue the message now
        try
        {
            await queries.QueueStreamMessageAsync(clusterOptions.ServiceId, Name, queueId, payload, streamOptions.ExpiryTimeout);
        }
        catch (Exception ex)
        {
            LogFailedToQueueStreamMessage(ex, clusterOptions.ServiceId, Name, queueId);
            throw;
        }
    }

    #region Logging

    [LoggerMessage(1, LogLevel.Error, "Failed to queue stream message with ({ServiceId}, {ProviderId}, {QueueId})")]
    private partial void LogFailedToQueueStreamMessage(Exception ex, string serviceId, string providerId, string queueId);

    #endregion Logging
}