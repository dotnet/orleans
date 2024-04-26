using System.Collections.Concurrent;

namespace Orleans.Streaming.AdoNet;

/// <summary>
/// Maps orleans stream and queue identifiers to ADO.NET queue identifiers.
/// </summary>
internal interface IAdoNetStreamQueueMapper
{
    /// <summary>
    /// Gets the ADO.NET queue identifier for the specified orleans stream identifier.
    /// </summary>
    string GetAdoNetQueueId(StreamId streamId);

    /// <summary>
    /// Gets the ADO.NET queue identifier for the specified orleans queue identifier.
    /// </summary>
    string GetAdoNetQueueId(QueueId queueId);
}

/// <summary>
/// Maps orleans streams and queues identifiers to ADO.NET queue identifiers.
/// </summary>
internal class AdoNetStreamQueueMapper(IConsistentRingStreamQueueMapper mapper) : IAdoNetStreamQueueMapper
{
    /// <summary>
    /// Caches the lookup of orleans stream identifiers to ADO.NET queue identifiers.
    /// </summary>
    private readonly ConcurrentDictionary<StreamId, string> _byStreamLookup = new();

    /// <summary>
    /// Caches the lookup of orleans queue identifiers to ADO.NET queue identifiers.
    /// </summary>
    private readonly ConcurrentDictionary<QueueId, string> _byQueueLookup = new();

    /// <summary>
    /// Caches the mapping factory of orleans stream identifiers to ADO.NET queue identifiers.
    /// </summary>
    private readonly Func<StreamId, string> _fromStreamFactory = (StreamId streamId) => mapper.GetQueueForStream(streamId).ToString();

    /// <summary>
    /// Cache the mapping factory of orleans queue identifiers to ADO.NET queue identifiers.
    /// </summary>
    private readonly Func<QueueId, string> _fromQueueFactory = (QueueId queueId) => queueId.ToString();

    /// <inheritdoc />
    public string GetAdoNetQueueId(StreamId streamId) => _byStreamLookup.GetOrAdd(streamId, _fromStreamFactory);

    /// <inheritdoc />
    public string GetAdoNetQueueId(QueueId queueId) => _byQueueLookup.GetOrAdd(queueId, _fromQueueFactory);
}