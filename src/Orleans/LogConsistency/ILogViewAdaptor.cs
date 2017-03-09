using Orleans.MultiCluster;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.LogConsistency
{
    /// <summary>
    /// A log view adaptor is the storage interface for <see cref="ILogConsistentGrain"/>, whose state is defined as a log view. 
    ///<para>
    /// There is one adaptor per grain, which is installed by <see cref="ILogConsistencyProvider"/> when the grain is activated.
    ///</para>
    /// </summary>
    /// <typeparam name="TLogView"> Type for the log view </typeparam>
    /// <typeparam name="TLogEntry"> Type for the log entry </typeparam>
    public interface ILogViewAdaptor<TLogView, TLogEntry> :
          ILogViewRead<TLogView, TLogEntry>,
          ILogViewUpdate<TLogEntry>,
          ILogConsistencyDiagnostics
        where TLogView : new()
    {
        /// <summary>Called during activation, right before the user-defined <see cref="Grain.OnActivateAsync"/>.</summary>
        Task PreOnActivate();

        /// <summary>Called during activation, right after the user-defined <see cref="Grain.OnActivateAsync"/>..</summary>
        Task PostOnActivate();

        /// <summary>Called during deactivation, right after the user-defined <see cref="Grain.OnDeactivateAsync"/>.</summary>
        Task PostOnDeactivate();

        /// <summary>Called when a grain receives a message from a remote instance.</summary>
        Task<ILogConsistencyProtocolMessage> OnProtocolMessageReceived(ILogConsistencyProtocolMessage payload);

        /// <summary>Called after the silo receives a new multi-cluster configuration.</summary>
        Task OnMultiClusterConfigurationChange(MultiClusterConfiguration next);
    }
}
