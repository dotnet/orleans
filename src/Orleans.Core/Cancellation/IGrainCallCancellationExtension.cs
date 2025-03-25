#nullable enable
using System.Threading.Tasks;
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

internal sealed class ExternalClientGrainCallCancellationManager(IInternalGrainFactory grainFactory) : IGrainCallCancellationManager
{
    public void SignalCancellation(SiloAddress? targetSilo, GrainId targetGrainId, GrainId sendingGrainId, CorrelationId messageId)
    {
        var targetGrain = grainFactory.GetGrain<IGrainCallCancellationExtension>(targetGrainId);
        targetGrain.CancelRequestAsync(sendingGrainId, messageId).Ignore();
    }
}
