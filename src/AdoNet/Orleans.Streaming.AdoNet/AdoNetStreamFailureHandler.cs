namespace Orleans.Streaming.AdoNet;

/// <summary>
/// Logs subscriber failures without mutating the shared retained log.
/// </summary>
internal partial class AdoNetStreamFailureHandler : IStreamFailureHandler
{
    private readonly ILogger<AdoNetStreamFailureHandler> _logger;

    public AdoNetStreamFailureHandler(
        bool faultOnFailure,
        AdoNetStreamOptions streamOptions,
        ClusterOptions clusterOptions,
        AdoNetStreamQueueMapper mapper,
        RelationalOrleansQueries queries,
        ILogger<AdoNetStreamFailureHandler> logger)
    {
        ShouldFaultSubsriptionOnError = faultOnFailure;
        _logger = logger;
        _ = streamOptions;
        _ = clusterOptions;
        _ = mapper;
        _ = queries;
    }

    /// <inheritdoc />
    public bool ShouldFaultSubsriptionOnError { get; }

    /// <inheritdoc />
    public Task OnDeliveryFailure(
        GuidId subscriptionId,
        string streamProviderName,
        StreamId streamIdentity,
        StreamSequenceToken? sequenceToken)
    {
        LogDeliveryFailure(_logger, subscriptionId, streamProviderName, streamIdentity, sequenceToken);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnSubscriptionFailure(
        GuidId subscriptionId,
        string streamProviderName,
        StreamId streamIdentity,
        StreamSequenceToken? sequenceToken)
    {
        LogSubscriptionFailure(_logger, subscriptionId, streamProviderName, streamIdentity, sequenceToken);
        return Task.CompletedTask;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "ADO.NET stream delivery failed for subscription {SubscriptionId} on provider {ProviderName}, stream {StreamId}, at {SequenceToken}. The retained source record was not modified.")]
    private static partial void LogDeliveryFailure(
        ILogger logger,
        GuidId subscriptionId,
        string providerName,
        StreamId streamId,
        StreamSequenceToken? sequenceToken);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "ADO.NET stream subscription {SubscriptionId} failed on provider {ProviderName}, stream {StreamId}, at {SequenceToken}. The retained source record was not modified.")]
    private static partial void LogSubscriptionFailure(
        ILogger logger,
        GuidId subscriptionId,
        string providerName,
        StreamId streamId,
        StreamSequenceToken? sequenceToken);
}
