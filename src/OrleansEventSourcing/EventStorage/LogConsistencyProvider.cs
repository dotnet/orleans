using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.LogConsistency;
using Orleans.Providers;
using Orleans.Storage;


namespace Orleans.EventSourcing.EventStorage
{
    /// <summary>
    /// A template for building event storage providers.
    /// Subclasses can extend and override the IEventStore interface.
    /// </summary>
    /// 
    public class LogConsistencyProvider : ILogConsistencyProvider
    {
        /// <inheritdoc/>
        public string Name { get; private set; }

        /// <inheritdoc/>
        public Logger Log { get; private set; }

        /// <summary>This provider uses its own event storage, it does not rely on a separate storage provider</summary>
        public bool UsesStorageProvider { get { return false; } }

        /// <summary>This provider requires an event storage provider for storing the events</summary>
        public bool UsesEventStorageProvider { get { return true; } }


        private static int counter; // used for constructing a unique id
        private int id;

        /// <inheritdoc/>
        protected virtual string GetLoggerName()
        {
            return string.Format("LogViews.{0}.{1}", GetType().Name, id);
        }


        internal IEventStorageProvider EventStore { get; private set; }

        internal IStorageProvider CheckpointStore { get; private set; }

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

            return Task.CompletedTask;
        }

        /// <summary>
        /// Close method
        /// </summary>
        public Task Close()
        {
            return Task.CompletedTask;
        }



        /// <inheritdoc/>
        public ILogViewAdaptor<TLogView, TLogEntry> MakeLogViewAdaptor<TLogView, TLogEntry>(ILogViewAdaptorHost<TLogView, TLogEntry> hostGrain, TLogView initialState, string grainTypeName, IStorageProvider storageProvider, IEventStorageProvider eventStorageProvider, ILogConsistencyProtocolServices services)
            where TLogView : class, new()
            where TLogEntry : class
        {
            CheckpointStore = storageProvider;
            EventStore = eventStorageProvider;
            return new EventStoreLogViewAdaptor<TLogView, TLogEntry>(hostGrain, this, initialState, services);
        }

     
    }


}
