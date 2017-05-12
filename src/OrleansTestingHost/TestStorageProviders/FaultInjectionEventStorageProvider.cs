
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Storage;
using System.Linq;

namespace Orleans.TestingHost
{
    /// <summary>
    /// Fault injection decorator for event storage providers.  
    /// This allows users to inject exceptions to test error handling scenarios.
    /// </summary>
    /// <typeparam name="TEventStorage">The wrapped event storage provider class</typeparam>
    public class FaultInjectionEventStorageProvider<TEventStorage> : IEventStorageProvider
        where TEventStorage : IEventStorageProvider, new()
    {
        private readonly TEventStorage wrappedProvider;
        private IGrainFactory grainFactory;
        /// <summary>The name of this provider instance, as given to it in the config.</summary>
        public string Name => wrappedProvider.Name;

        /// <summary>Logger used by this storage provider instance.</summary>
        /// <returns>Reference to the Logger object used by this provider.</returns>
        /// <seealso cref="Logger"/>
        public Logger Log { get; private set; }

        /// <summary>
        /// Default conststructor which creates the decorated storage provider
        /// </summary>
        public FaultInjectionEventStorageProvider()
        {
            wrappedProvider = new TEventStorage();
        }

        /// <summary>  Name of the property that controls the inserted delay. </summary>
        public const string DelayMillisecondsPropertyName = "DelayMilliseconds";

        private int delayMilliseconds;

        /// <summary>
        /// Initializes the decorated event-storage provider.
        /// </summary>
        /// <param name="name">Name assigned for this provider</param>
        /// <param name="providerRuntime">Callback for accessing system functions in the Provider Runtime</param>
        /// <param name="config">Configuration metadata to be used for this provider instance</param>
        /// <returns>Completion promise Task for the inttialization work for this provider</returns>
        public async Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            grainFactory = providerRuntime.GrainFactory;
            await wrappedProvider.Init(name, providerRuntime, config);
            Log = wrappedProvider.Log.GetSubLogger("FaultInjection");
            Log.Info($"Initialized fault injection for event-storage provider {Name}");

            string value;
            if (config.Properties.TryGetValue(DelayMillisecondsPropertyName, out value))
                delayMilliseconds = int.Parse(value);
        }

        /// <summary>Close function for this provider instance.</summary>
        /// <returns>Completion promise for the Close operation on this provider.</returns>
        public async Task Close()
        {
            await wrappedProvider.Close();
        }

        // delays for specified duration, and injects next exception in queue
        private async Task DelayAndInject(string streamName)
        {
            if (delayMilliseconds > 0)
                await Task.Delay(delayMilliseconds);

            try
            {
                var faultGrain = grainFactory.GetGrain<IEventStorageFaultGrain>(streamName);
                await faultGrain.Next();
            }
            catch (Exception)
            {
                Log.Info($"Fault injected for event stream {streamName}");
                throw;
            }
        }

        /// <inheritdoc/>
        public string DefaultStreamName(Type grainType, GrainReference grainReference)
        {
            return wrappedProvider.DefaultStreamName(grainType, grainReference);
        }

        /// <inheritdoc/>
        public IEventStreamHandle GetEventStreamHandle(string streamName)
        {
            return new EventStreamWrapper(wrappedProvider.GetEventStreamHandle(streamName), this);
        }

        private class EventStreamWrapper : IEventStreamHandle
        {

            public EventStreamWrapper(IEventStreamHandle wrappedHandle, FaultInjectionEventStorageProvider<TEventStorage> provider)
            {
                this.wrappedHandle = wrappedHandle;
                this.provider = provider;
            }

            private readonly IEventStreamHandle wrappedHandle;
            private readonly FaultInjectionEventStorageProvider<TEventStorage> provider;

            private Logger Log {  get { return provider.Log; } }
            public string StreamName { get { return wrappedHandle.StreamName; } }

     
            public void Dispose()
            {
                wrappedHandle.Dispose();
            }

            /// <inheritdoc/>
            public async Task<int> GetVersion()
            {
                Log.Info($"GetVersion({wrappedHandle.StreamName})");
                await provider.DelayAndInject(wrappedHandle.StreamName);
                return await wrappedHandle.GetVersion();
            }

            /// <inheritdoc/>
            public async Task<EventStreamSegment<E>> Load<E>(int startAtVersion = 0, int? endAtVersion = default(int?))
            {
                Log.Info($"Load({wrappedHandle.StreamName},{startAtVersion},{endAtVersion})");
                await provider.DelayAndInject(wrappedHandle.StreamName);
                return await wrappedHandle.Load<E>(startAtVersion, endAtVersion);
            }

            /// <inheritdoc/>
            public async Task<bool> Append<E>(IEnumerable<KeyValuePair<Guid, E>> events, int? expectedVersion = default(int?))
            {
                Log.Info($"Append({wrappedHandle.StreamName},{events.Count()} events,{expectedVersion})");
                await provider.DelayAndInject(wrappedHandle.StreamName);
                return await wrappedHandle.Append(events, expectedVersion);
            }

            /// <inheritdoc/>
            public async Task<bool> Delete(int? expectedVersion = default(int?))
            {
                Log.Info($"Delete({wrappedHandle.StreamName},{expectedVersion})");
                await provider.DelayAndInject(wrappedHandle.StreamName);
                return await wrappedHandle.Delete(expectedVersion);
            }
        }
    }
}
