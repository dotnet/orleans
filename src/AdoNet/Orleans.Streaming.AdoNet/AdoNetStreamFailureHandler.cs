using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Streaming.AdoNet.Storage;

namespace Orleans.Streaming.AdoNet;

/// <summary>
/// An <see cref="IStreamFailureHandler"/> that attempts to move the message to dead letters.
/// </summary>
internal partial class AdoNetStreamFailureHandler(bool faultOnFailure, AdoNetStreamOptions streamOptions, ClusterOptions clusterOptions, IAdoNetStreamQueueMapper mapper, RelationalOrleansQueries queries, ILogger<AdoNetStreamFailureHandler> logger) : IStreamFailureHandler
{
    private readonly ILogger<AdoNetStreamFailureHandler> _logger = logger;

    /// <summary>
    /// Gets a value indicating whether the subscription should fault when there is an error.
    /// </summary>
    public bool ShouldFaultSubsriptionOnError { get; } = faultOnFailure;

    /// <summary>
    /// Attempts to move the message to dead letters on delivery failure.
    /// </summary>
    public Task OnDeliveryFailure(GuidId subscriptionId, string streamProviderName, StreamId streamIdentity, StreamSequenceToken sequenceToken)
    {
        ArgumentNullException.ThrowIfNull(streamProviderName);
        ArgumentNullException.ThrowIfNull(sequenceToken);

        return OnFailureAsync(streamProviderName, streamIdentity, sequenceToken);
    }

    /// <summary>
    /// Attempts to move the message to dead letters on delivery failure.
    /// </summary>
    public Task OnSubscriptionFailure(GuidId subscriptionId, string streamProviderName, StreamId streamIdentity, StreamSequenceToken sequenceToken)
    {
        ArgumentNullException.ThrowIfNull(streamProviderName);
        ArgumentNullException.ThrowIfNull(sequenceToken);

        return OnFailureAsync(streamProviderName, streamIdentity, sequenceToken);
    }

    /// <summary>
    /// Attempts to move the message to dead letters on delivery failure.
    /// </summary>
    private async Task OnFailureAsync(string streamProviderName, StreamId streamIdentity, StreamSequenceToken sequenceToken)
    {
        var queueId = mapper.GetAdoNetQueueId(streamIdentity);

        try
        {
            await queries.MoveMessageToDeadLettersAsync(clusterOptions.ServiceId, streamProviderName, queueId, sequenceToken.SequenceNumber, streamOptions.MaxAttempts, streamOptions.RemovalTimeout);

            LogMovedMessage(clusterOptions.ServiceId, streamProviderName, queueId, sequenceToken.SequenceNumber);
        }
        catch (Exception ex)
        {
            LogFailedToMoveMessage(ex, clusterOptions.ServiceId, streamProviderName, queueId, sequenceToken.SequenceNumber);
        }
    }

    #region Logging

    [LoggerMessage(1, LogLevel.Warning, "Moved failed delivery to dead letters: ({ServiceId}, {ProviderId}, {QueueId}, {MessageId})")]
    private partial void LogMovedMessage(string serviceId, string providerId, string queueId, long messageId);

    [LoggerMessage(2, LogLevel.Error, "Failed to move failed delivery to dead letters: ({ServiceId}, {ProviderId}, {QueueId}, {MessageId}")]
    private partial void LogFailedToMoveMessage(Exception ex, string serviceId, string providerId, string queueId, long messageId);

    #endregion Logging
}