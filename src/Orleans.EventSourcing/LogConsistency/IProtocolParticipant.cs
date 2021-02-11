using System.Threading.Tasks;

namespace Orleans.EventSourcing
{
    /// <summary>
    /// Grain interface for grains that participate in multi-cluster log-consistency protocols.
    /// </summary>
    public interface ILogConsistencyProtocolParticipant  : IGrain  
    {
        /// <summary>
        /// Called immediately before the user-level OnActivateAsync, on same scheduler.
        /// </summary>
        /// <returns></returns>
        Task PreActivateProtocolParticipant();

        /// <summary>
        /// Called immediately after the user-level OnActivateAsync, on same scheduler.
        /// </summary>
        /// <returns></returns>
        Task PostActivateProtocolParticipant();

        /// <summary>
        /// Called immediately after the user-level OnDeactivateAsync, on same scheduler.
        /// </summary>
        /// <returns></returns>
        Task DeactivateProtocolParticipant();
    }

    /// <summary>
    /// interface to mark classes that represent protocol messages.
    /// All such classes must be serializable.
    /// </summary>
    public interface ILogConsistencyProtocolMessage
    {
    }
}
