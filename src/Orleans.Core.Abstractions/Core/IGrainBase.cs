using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans
{
    /// <summary>
    /// Interface for grain implementations
    /// </summary>
    public interface IGrainBase
    {
        /// <summary>
        /// Gets the grain context.
        /// </summary>
        IGrainContext GrainContext { get; }

        /// <summary>
        /// Method overridden by grain implementations to handle activation.
        /// </summary>
        /// <param name="token">The cancellation token used to signify that activation should abort promptly.</param>
        /// <returns>A <see cref="Task"/> which represents the operation.</returns>
        Task OnActivateAsync(CancellationToken token) => Task.CompletedTask;

        /// <summary>
        /// Method overridden by grain implementations to handle deactivation.
        /// </summary>
        /// <param name="reason">The reason for deactivation.</param>
        /// <param name="token">The cancellation token used to signify that deactivation should complete promptly.</param>
        /// <returns>A <see cref="Task"/> which represents the operation.</returns>
        Task OnDeactivateAsync(DeactivationReason reason, CancellationToken token) => Task.CompletedTask;
    }

    /// <summary>
    /// Helper methods for <see cref="IGrainBase"/> implementations.
    /// </summary>
    public static class GrainBaseExtensions
    {
        /// <summary>
        /// Deactivate this activation of the grain after the current grain method call is completed.
        /// This call will mark this activation of the current grain to be deactivated and removed at the end of the current method.
        /// The next call to this grain will result in a different activation to be used, which typical means a new activation will be created automatically by the runtime.
        /// </summary>
        public static void DeactivateOnIdle(this IGrainBase grain) => 
            grain.GrainContext.Deactivate(new(DeactivationReasonCode.ApplicationRequested, $"{nameof(DeactivateOnIdle)} was called."));
    }

    /// <summary>
    /// An informational reason code for deactivation.
    /// </summary>
    [GenerateSerializer]
    public enum DeactivationReasonCode : byte
    {
        /// <summary>
        /// No reason provided.
        /// </summary>
        None,

        /// <summary>
        /// The process is currently shutting down.
        /// </summary>
        ShuttingDown,

        /// <summary>
        /// Activation of the grain failed.
        /// </summary>
        ActivationFailed,

        /// <summary>
        /// This activation is affected by an internal failure.
        /// </summary>
        /// <remarks>
        /// This could be caused by the failure of a process hosting this activation's grain directory partition, for example.
        /// </remarks>
        InternalFailure,

        /// <summary>
        /// This activation is idle.
        /// </summary>
        ActivationIdle,

        /// <summary>
        /// This activation is unresponsive to commands or requests.
        /// </summary>
        ActivationUnresponsive,

        /// <summary>
        /// Another instance of this grain has been activated.
        /// </summary>
        DuplicateActivation,

        /// <summary>
        /// This activation received a request which cannot be handled by the locally running process.
        /// </summary>
        IncompatibleRequest,

        /// <summary>
        /// An application error occurred.
        /// </summary>
        ApplicationError,

        /// <summary>
        /// The application requested that this activation deactivate.
        /// </summary>
        ApplicationRequested,
    }
}
