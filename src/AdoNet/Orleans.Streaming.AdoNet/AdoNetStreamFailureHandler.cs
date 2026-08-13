namespace Orleans.Streaming.AdoNet;

/// <summary>
/// A placeholder <see cref="IStreamFailureHandler"/> for the retained-log receiver integration.
/// </summary>
internal class AdoNetStreamFailureHandler : IStreamFailureHandler
{
    public AdoNetStreamFailureHandler(
        bool faultOnFailure,
        AdoNetStreamOptions streamOptions,
        ClusterOptions clusterOptions,
        AdoNetStreamQueueMapper mapper,
        RelationalOrleansQueries queries,
        ILogger<AdoNetStreamFailureHandler> logger)
    {
        ShouldFaultSubsriptionOnError = faultOnFailure;
        _ = streamOptions;
        _ = clusterOptions;
        _ = mapper;
        _ = queries;
        _ = logger;
    }

    /// <summary>
    /// Gets a value indicating whether the subscription should fault when there is an error.
    /// </summary>
    public bool ShouldFaultSubsriptionOnError { get; }

    /// <summary>
    /// Failure handling is supplied by the shared recoverable stream pipeline.
    /// </summary>
    public Task OnDeliveryFailure(GuidId subscriptionId, string streamProviderName, StreamId streamIdentity, StreamSequenceToken? sequenceToken) =>
        Task.FromException(CreatePipelineUnavailableException());

    /// <summary>
    /// Failure handling is supplied by the shared recoverable stream pipeline.
    /// </summary>
    public Task OnSubscriptionFailure(GuidId subscriptionId, string streamProviderName, StreamId streamIdentity, StreamSequenceToken? sequenceToken) =>
        Task.FromException(CreatePipelineUnavailableException());

    private static InvalidOperationException CreatePipelineUnavailableException() =>
        new("The ADO.NET retained-log failure policy requires the shared recoverable stream cache and receiver pipeline.");
}