using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Providers;
using Orleans.Runtime;
using System.Collections.Concurrent;

namespace Orleans.Storage
{
    /// <summary>
    /// This is a simple in-memory grain implementation of a event-storage provider.
    /// </summary>
    /// <remarks>
    /// This storage provider is ONLY intended for simple in-memory Development / Unit Test scenarios.
    /// This class should NOT be used in Production environment, 
    ///  because [by-design] it does not provide any resilience 
    ///  or long-term persistence capabilities.
    /// </remarks>
    /// <example>
    /// Example configuration for this storage provider in OrleansConfiguration.xml file:
    /// <code>
    /// &lt;OrleansConfiguration xmlns="urn:orleans">
    ///   &lt;Globals>
    ///     &lt;EventStorageProviders>
    ///       &lt;Provider Type="Orleans.Storage.MemoryEventStorage" Name="MemoryEventStore" />
    ///   &lt;/EventStorageProviders>
    /// </code>
    /// </example>
    [DebuggerDisplay("MemoryEventStore:{Name}")]
    public class MemoryEventStorage : IEventStorageProvider
    {
        /// <summary>
        /// Default number of event storage grains per type.
        /// </summary>
        public const int NumStorageGrainsPerTypeDefaultValue = 10;
        /// <summary>
        /// Config string name for number of queue storage grains.
        /// </summary>
        public const string NumStorageGrainsPerTypePropertyName = "NumStorageGrainsPerType";
        private int numStorageGrainsPerType;
        private static int counter;
        private readonly int id;
        private const string STATE_STORE_NAME = "MemoryEventStorage";
        private ConcurrentDictionary<Type, object> storageGrains;
        private IProviderRuntime providerRuntime;

        /// <summary> Name of this storage provider instance. </summary>
        /// <see cref="IProvider.Name"/>
        public string Name { get; private set; }

        /// <summary> Logger used by this storage provider instance. </summary>
        /// <see cref="IStorageProvider.Log"/>
        public Logger Log { get; private set; }

        /// <summary> Default constructor. </summary>
        public MemoryEventStorage()
            : this(NumStorageGrainsPerTypeDefaultValue)
        {
        }

        /// <summary> Constructor - use the specificed number of store grains. </summary>
        /// <param name="numStoreGrainsPerType">Number of store grains to use.</param>
        protected MemoryEventStorage(int numStoreGrainsPerType)
        {
            id = Interlocked.Increment(ref counter);
            numStorageGrainsPerType = numStoreGrainsPerType;
        }

        private string GetLoggerName()
        {
            return string.Format("Storage.{0}.{1}", GetType().Name, id);
        }

        /// <summary> Initialization function for this storage provider. </summary>
        /// <see cref="IProvider.Init"/>
        public virtual Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            Name = name;
            Log = providerRuntime.GetLogger(GetLoggerName());
            this.providerRuntime = providerRuntime;

            string numStorageGrainsStr;
            if (config.Properties.TryGetValue(NumStorageGrainsPerTypePropertyName, out numStorageGrainsStr))
                numStorageGrainsPerType = Int32.Parse(numStorageGrainsStr);

            Log.Info("Init: Name={0} NumStorageGrainsPerType={1}", Name, numStorageGrainsPerType);

            storageGrains = new ConcurrentDictionary<Type, object>();

            return Task.CompletedTask;
        }

        /// <summary> Shutdown function for this storage provider. </summary>
        public virtual Task Close()
        {
            storageGrains.Clear();

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public string DefaultStreamName(Type grainType, GrainReference grainReference)
        {
            var keys = new[]
            {
                Tuple.Create("GrainType", grainType.FullName),
                Tuple.Create("GrainId", grainReference.ToKeyString())
            };
            return HierarchicalKeyStore.MakeStoreKey(keys);
        }

        /// <inheritdoc />
        public IEventStreamHandle<TEvent> GetEventStreamHandle<TEvent>(string streamName)
        {
            var type = typeof(TEvent);

            // get the grain array for this type
            IMemoryEventStorageGrain<TEvent>[] grainArray;
            if (storageGrains.ContainsKey(type))
            {
                grainArray = (IMemoryEventStorageGrain<TEvent>[])storageGrains[type];
            }
            else
            {
                grainArray = new IMemoryEventStorageGrain<TEvent>[numStorageGrainsPerType];
                for (int i = 0; i < numStorageGrainsPerType; i++)
                {
                    grainArray[i] = providerRuntime.GrainFactory.GetGrain<IMemoryEventStorageGrain<TEvent>>(i);
                }
                storageGrains[type] = grainArray;
            }

            // get the grain for this stream
            int idx = StorageProviderUtils.PositiveHash(id.GetHashCode(), numStorageGrainsPerType);
            IMemoryEventStorageGrain<TEvent> storageGrain = grainArray[idx];

            // create a stream handle
            return new EventStreamHandle<TEvent>(streamName, storageGrain, Log);
        }


        private class EventStreamHandle<TEvent> : IEventStreamHandle<TEvent>
        {

            public EventStreamHandle(string streamName, IMemoryEventStorageGrain<TEvent> storageGrain, Logger log)
            {
                this.streamName = streamName;
                this.storageGrain = storageGrain;
                this.log = log;
            }

            private readonly string streamName;
            private readonly IMemoryEventStorageGrain<TEvent> storageGrain;
            private readonly Logger log;

            public string StreamName { get { return streamName; } }

            public void Dispose() { }

            public Task<int> GetVersion()
            {
                if (log.IsVerbose2) log.Verbose2($"GetVersion stream={streamName}");
                return storageGrain.GetVersion(streamName);
            }

            public Task<EventStreamSegment<TEvent>> Load(int startAtVersion = 0, int? endAtVersion = default(int?))
            {
                if (log.IsVerbose2) log.Verbose2($"Load stream={streamName} start={startAtVersion} end={endAtVersion}");

                // call the grain that contains the storage
                return storageGrain.Load(streamName, startAtVersion, endAtVersion);
            }

            public Task<bool> Append(IEnumerable<KeyValuePair<Guid, TEvent>> events, int? expectedVersion = default(int?))
            {
                if (log.IsVerbose2) log.Verbose2($"Append stream={streamName} events=[{string.Join(",", events.Select(e => e.Key.ToString()))}] expectedVersion={expectedVersion}");

                // call the grain that contains the event storage
                return storageGrain.Append(streamName, events, expectedVersion);
            }

            public Task<bool> Delete(int? expectedVersion = default(int?))
            {
                if (log.IsVerbose2) log.Verbose2($"Delete stream={streamName} expectedVersion={expectedVersion}");

                // call the grain that contains the event storage
                return storageGrain.Delete(streamName, expectedVersion);
            }
        }
    }
}
