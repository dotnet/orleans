using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using Orleans.LogConsistency;
using Orleans.Runtime;
using Orleans.Storage;
using System.Threading;
using Orleans.Providers;

namespace Orleans.EventSourcing.StateStorage
{
    /// <summary>
    /// A log-consistency provider that stores the latest view in primary storage, using any standard storage provider.
    /// Supports multiple clusters connecting to the same primary storage (doing optimistic concurrency control via e-tags)
    ///<para>
    /// The log itself is transient, i.e. not actually saved to storage - only the latest view (snapshot) and some 
    /// metadata (the log position, and write flags) are stored in the primary. 
    /// </para>
    /// </summary>
    public class LogConsistencyProvider : ILogConsistencyProvider
    {
        /// <inheritdoc/>
        public string Name { get; private set; }

        /// <inheritdoc/>
        public Logger Log { get; private set; }

        /// <inheritdoc/>
        public bool UsesStorageProvider
        {
            get
            {
                return true;
            }
        }

        private static int counter; // used for constructing a unique id
        private int id;

        /// <inheritdoc/>
        protected virtual string GetLoggerName()
        {
            return string.Format("LogViews.{0}.{1}", GetType().Name, id);
        }

        /// <summary>
        /// Init method
        /// </summary>
        /// <param name="name">Consistency provider name</param>
        /// <param name="providerRuntime">Provider runtime</param>
        /// <param name="config">Provider config</param>
        public Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            Name = name;
            id = Interlocked.Increment(ref counter); // unique id for this provider; matters only for tracing

            Log = providerRuntime.GetLogger(GetLoggerName());
            Log.Info("Init (Severity={0})", Log.SeverityLevel);

            return TaskDone.Done;
        }
      
        /// <summary>
        /// Close method
        /// </summary>
        public Task Close()
        {
            return TaskDone.Done;
        }

        /// <summary>
        /// Make log view adaptor 
        /// </summary>
        /// <typeparam name="TView">The type of the view</typeparam>
        /// <typeparam name="TEntry">The type of the log entries</typeparam>
        /// <param name="hostGrain">The grain that is hosting this adaptor</param>
        /// <param name="initialState">The initial state for this view</param>
        /// <param name="grainTypeName">The type name of the grain</param>
        /// <param name="services">Runtime services for multi-cluster coherence protocols</param>
        public ILogViewAdaptor<TView, TEntry> MakeLogViewAdaptor<TView, TEntry>(ILogViewAdaptorHost<TView, TEntry> hostGrain, TView initialState, string grainTypeName, IStorageProvider storageProvider, ILogConsistencyProtocolServices services) 
            where TView : class, new()
            where TEntry : class
        {
            return new LogViewAdaptor<TView,TEntry>(hostGrain, initialState, storageProvider, grainTypeName, services);
        }

    }

}