using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Providers;
using Orleans.Runtime;

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
        /// Default number of queue storage grains.
        /// </summary>
        public const int NumStorageGrainsDefaultValue = 10;
        /// <summary>
        /// Config string name for number of queue storage grains.
        /// </summary>
        public const string NumStorageGrainsPropertyName = "NumStorageGrains";
        private int numStorageGrains;
        private static int counter;
        private readonly int id;
        private const string STATE_STORE_NAME = "MemoryEventStorage";
        private Lazy<IMemoryEventStorageGrain>[] storageGrains;

        /// <summary> Name of this storage provider instance. </summary>
        /// <see cref="IProvider.Name"/>
        public string Name { get; private set; }

        /// <summary> Logger used by this storage provider instance. </summary>
        /// <see cref="IStorageProvider.Log"/>
        public Logger Log { get; private set; }

        /// <summary> Default constructor. </summary>
        public MemoryEventStorage()
            : this(NumStorageGrainsDefaultValue)
        {
        }

        /// <summary> Constructor - use the specificed number of store grains. </summary>
        /// <param name="numStoreGrains">Number of store grains to use.</param>
        protected MemoryEventStorage(int numStoreGrains)
        {
            id = Interlocked.Increment(ref counter);
            numStorageGrains = numStoreGrains;
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

            string numStorageGrainsStr;
            if (config.Properties.TryGetValue(NumStorageGrainsPropertyName, out numStorageGrainsStr))
                numStorageGrains = Int32.Parse(numStorageGrainsStr);

            Log.Info("Init: Name={0} NumStorageGrains={1}", Name, numStorageGrains);

            storageGrains = new Lazy<IMemoryEventStorageGrain>[numStorageGrains];
            for (int i = 0; i < numStorageGrains; i++)
            {
                int idx = i; // Capture variable to avoid modified closure error
                storageGrains[idx] = new Lazy<IMemoryEventStorageGrain>(() => providerRuntime.GrainFactory.GetGrain<IMemoryEventStorageGrain>(idx));
            }
            return TaskDone.Done;
        }

        /// <summary> Shutdown function for this storage provider. </summary>
        public virtual Task Close()
        {
            for (int i = 0; i < numStorageGrains; i++)
                storageGrains[i] = null;

            return TaskDone.Done;
        }

        private IMemoryEventStorageGrain GetStorageGrain(string id)
        {

            int idx = StorageProviderUtils.PositiveHash(id.GetHashCode(), numStorageGrains);
            IMemoryEventStorageGrain storageGrain = storageGrains[idx].Value;
            return storageGrain;
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
        public IEventStreamHandle GetEventStreamHandle(string streamName)
        {
            return new EventStreamHandle(streamName, GetStorageGrain(streamName), Log);
        }



        private class EventStreamHandle : IEventStreamHandle
        {

            public EventStreamHandle(string streamName, IMemoryEventStorageGrain storageGrain, Logger log)
            {
                this.streamName = streamName;
                this.storageGrain = storageGrain;
                this.log = log;
            }

            private readonly string streamName;
            private readonly IMemoryEventStorageGrain storageGrain;
            private readonly Logger log;

            public string StreamName { get { return streamName; } }

            public void Dispose() { }

            public Task<int> GetVersion()
            {
                if (log.IsVerbose2) log.Verbose2($"GetVersion stream={streamName}");
                return storageGrain.GetVersion(streamName);
            }

            public async Task<EventStreamSegment<E>> Load<E>(int startAtVersion = 0, int? endAtVersion = default(int?))
            {
                if (log.IsVerbose2) log.Verbose2($"Load stream={streamName} start={startAtVersion} end={endAtVersion}");

                // call the grain that contains the storage
                var response = await storageGrain.Load(streamName, startAtVersion, endAtVersion);

                // must convert returned objects to type E
                return new EventStreamSegment<E>()
                {
                    StreamName = response.StreamName,
                    FromVersion = response.FromVersion,
                    ToVersion = response.ToVersion,
                    Events = response.Events.Select(kvp => new KeyValuePair<Guid, E>(kvp.Key, (E)kvp.Value)).ToList(),
                };

            }

            public Task<bool> Append<E>(IEnumerable<KeyValuePair<Guid, E>> events, int? expectedVersion = default(int?))
            {
                var eventArray = events.Select(kvp => new KeyValuePair<Guid, object>(kvp.Key, kvp.Value)).ToArray();
                if (log.IsVerbose2) log.Verbose2($"Append stream={streamName} events=[{string.Join(",", eventArray.Select(e => e.Key.ToString()))}] expectedVersion={expectedVersion}");

                // call the grain that contains the event storage
                return storageGrain.Append(streamName, eventArray, expectedVersion);
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
