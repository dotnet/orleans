namespace Orleans.Streaming.AdoNet;

/// <summary>
/// Maps Orleans streams and queues identifiers to ADO.NET queue identifiers.
/// </summary>
internal class AdoNetStreamQueueMapper(IConsistentRingStreamQueueMapper mapper)
{
    /// <summary>
    /// Caches the lookup of Orleans stream identifiers to ADO.NET queue identifiers.
    /// </summary>
    private readonly ConcurrentDictionary<StreamId, string> _byStreamLookup = new();

    /// <summary>
    /// Caches the lookup of Orleans queue identifiers to ADO.NET queue identifiers.
    /// </summary>
    private readonly ConcurrentDictionary<QueueId, string> _byQueueLookup = new();

    /// <summary>
    /// Caches the mapping factory of Orleans stream identifiers to ADO.NET queue identifiers.
    /// </summary>
    private readonly Func<StreamId, string> _fromStreamFactory = (StreamId streamId) => mapper.GetQueueForStream(streamId).ToString();

    /// <summary>
    /// Cache the mapping factory of Orleans queue identifiers to ADO.NET queue identifiers.
    /// </summary>
    private readonly Func<QueueId, string> _fromQueueFactory = (QueueId queueId) => queueId.ToString();

    /// <summary>
    /// Gets the ADO.NET QueueId for the specified Orleans StreamId.
    /// </summary>
    public string GetAdoNetQueueId(StreamId streamId) => _byStreamLookup.GetOrAdd(streamId, _fromStreamFactory);

    /// <summary>
    /// Gets the ADO.NET QueueId for the specified Orleans QueueId.
    /// </summary>
    public string GetAdoNetQueueId(QueueId queueId) => _byQueueLookup.GetOrAdd(queueId, _fromQueueFactory);

    /// <summary>
    /// Gets all ADO.NET QueueIds in the current space.
    /// </summary>
    public IEnumerable<string> GetAllAdoNetQueueIds()
    {
        foreach (var queueId in mapper.GetAllQueues())
        {
            yield return _byQueueLookup.GetOrAdd(queueId, _fromQueueFactory);
        }
    }
}