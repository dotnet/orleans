#nullable enable
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Internal;

namespace Orleans.Runtime;

internal interface IGrainCallCancellationExtension : IGrainExtension
{
    /// <summary>
    /// Indicates that a cancellation token has been canceled.
    /// </summary>
    /// <param name="senderGrainId">
    /// The <see cref="GrainId"/> of the original message sender.
    /// </param>
    /// <param name="messageId">
    /// The message id of the request message being canceled.
    /// </param>
    /// <returns>
    /// A <see cref="Task"/> representing the operation.
    /// </returns>
    [AlwaysInterleave, OneWay]
    ValueTask CancelRequestAsync(GrainId senderGrainId, CorrelationId messageId);
}

/// <summary>
/// Functionality for cancelling grain calls.
/// </summary>
internal interface IGrainCallCancellationManager
{
    /// <summary>
    /// Attempts to cancel a grain call.
    /// </summary>
    void SignalCancellation(SiloAddress? targetSilo, GrainId targetGrainId, GrainId sendingGrainId, CorrelationId messageId);
}

internal sealed partial class ExternalClientGrainCallCancellationManager(IInternalGrainFactory grainFactory, ILogger<ExternalClientGrainCallCancellationManager> logger) : IGrainCallCancellationManager
{
    public void SignalCancellation(SiloAddress? targetSilo, GrainId targetGrainId, GrainId sendingGrainId, CorrelationId messageId)
    {
        LogDebugSignallingCancellation(logger, messageId, sendingGrainId, targetGrainId, targetSilo);

        var targetGrain = grainFactory.GetGrain<IGrainCallCancellationExtension>(targetGrainId);
        targetGrain.CancelRequestAsync(sendingGrainId, messageId).Ignore();
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Signalling cancellation for message {MessageId} from {SendingGrainId} to target grain {TargetGrainId} on silo {TargetSilo}"
    )]
    private static partial void LogDebugSignallingCancellation(ILogger logger, CorrelationId messageId, GrainId sendingGrainId, GrainId targetGrainId, SiloAddress? targetSilo);
}
