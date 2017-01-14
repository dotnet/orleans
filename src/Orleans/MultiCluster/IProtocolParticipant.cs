using System.Threading.Tasks;
using Orleans.Concurrency;

namespace Orleans.MultiCluster
{
    /// <summary>
    /// Grain interface for grains that participate in multi-cluster log-consistency protocols.
    /// </summary>
    public interface ILogConsistencyProtocolParticipant  : IGrain  
    {
        /// <summary>
        /// Called when a message is received from another cluster.
        /// This MUST interleave with other calls to avoid deadlocks.
        /// </summary>
        /// <param name="payload">the protocol message to be delivered</param>
        /// <returns></returns>
        [AlwaysInterleave]
        Task<ILogConsistencyProtocolMessage> OnProtocolMessageReceived(ILogConsistencyProtocolMessage payload);

        /// <summary>
        /// Called when a configuration change notification is received.
        /// </summary>
        /// <param name="next">the next multi-cluster configuration</param>
        /// <returns></returns>
        [AlwaysInterleave]
        Task OnMultiClusterConfigurationChange(MultiClusterConfiguration next);


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
