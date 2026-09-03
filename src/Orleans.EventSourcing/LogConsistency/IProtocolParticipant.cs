using System.Threading;
using System.Threading.Tasks;

namespace Orleans.EventSourcing
{
    /// <summary>
    /// Grain interface for grains that participate in multi-cluster log-consistency protocols.
    /// </summary>
    public interface ILogConsistencyProtocolParticipant : IGrain
    {
        /// <summary>
        /// Called immediately before the user-level OnActivateAsync, on same scheduler.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        [Alias("PreActivateProtocolParticipant")]
        Task PreActivateProtocolParticipant();

        /// <summary>
        /// Called immediately before the user-level OnActivateAsync, on the same scheduler.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        [Alias("0DB087C8")]
        Task PreActivateProtocolParticipant(CancellationToken cancellationToken)
            => PreActivateProtocolParticipant();

        /// <summary>
        /// Called immediately after the user-level OnActivateAsync, on same scheduler.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        [Alias("PostActivateProtocolParticipant")]
        Task PostActivateProtocolParticipant();

        /// <summary>
        /// Called immediately after the user-level OnActivateAsync, on the same scheduler.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        [Alias("22FD7D72")]
        Task PostActivateProtocolParticipant(CancellationToken cancellationToken)
            => PostActivateProtocolParticipant();

        /// <summary>
        /// Called immediately after the user-level OnDeactivateAsync, on same scheduler.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        [Alias("DeactivateProtocolParticipant")]
        Task DeactivateProtocolParticipant();

        /// <summary>
        /// Called immediately after the user-level OnDeactivateAsync, on the same scheduler.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        [Alias("A36FC884")]
        Task DeactivateProtocolParticipant(CancellationToken cancellationToken)
            => DeactivateProtocolParticipant();
    }

    /// <summary>
    /// interface to mark classes that represent protocol messages.
    /// All such classes must be serializable.
    /// </summary>
    public interface ILogConsistencyProtocolMessage
    {
    }
}
