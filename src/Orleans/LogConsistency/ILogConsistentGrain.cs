using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.LogConsistency
{
    /// <summary>
    ///  This interface encapsulates functionality of grains that manage their state
    ///  based on log consistency, such as JournaledGrain.
    ///  It is the equivalent of <see cref="IStatefulGrain"/> for log-consistent grains.
    /// </summary>
    public interface ILogConsistentGrain 
    {
        /// <summary>
        /// called right after grain construction to install the log view adaptor 
        /// </summary>
        /// <param name="factory"> The adaptor factory to use </param>
        /// <param name="state"> The initial state of the view </param>
        /// <param name="grainTypeName"> The type name of the grain </param>
        /// <param name="storageProvider"> The storage provider, if needed </param>
        /// <param name="services"> Protocol services </param>
        void InstallAdaptor(ILogViewAdaptorFactory factory, object state, string grainTypeName, IStorageProvider storageProvider, ILogConsistencyProtocolServices services);

        /// <summary>
        /// Gets the default adaptor factory to use, or null if there is no default 
        /// (in which case user MUST configure a consistency provider)
        /// </summary>
        ILogViewAdaptorFactory DefaultAdaptorFactory { get; }
     }


    /// <summary>
    /// Base class for all grains that use log-consistency for managing  the state.
    /// It is the equivalent of <see cref="Grain{T}"/> for grains using log-consistency.
    /// (SiloAssemblyLoader uses it to extract type)
    /// </summary>
    /// <typeparam name="TView">The type of the view</typeparam>
    public class LogConsistentGrainBase<TView> : Grain
    {
    }
}
