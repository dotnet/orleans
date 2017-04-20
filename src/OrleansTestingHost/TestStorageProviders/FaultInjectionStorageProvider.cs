
using System;
using System.Threading.Tasks;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.TestingHost
{
    /// <summary>
    /// Fault injection decorator for storage providers.  This allows users to inject storage exceptions to test error handling scenarios.
    /// </summary>
    /// <typeparam name="TStorage"></typeparam>
    public class FaultInjectionStorageProvider<TStorage> : IStorageProvider
        where TStorage : IStorageProvider, new()
    {
        private readonly TStorage realStorageProvider;
        private IGrainFactory grainFactory;
        /// <summary>The name of this provider instance, as given to it in the config.</summary>
        public string Name => realStorageProvider.Name;

        /// <summary>Logger used by this storage provider instance.</summary>
        /// <returns>Reference to the Logger object used by this provider.</returns>
        /// <seealso cref="Logger"/>
        public Logger Log { get; private set; }
        
        /// <summary>
        /// Default conststructor which creates the decorated storage provider
        /// </summary>
        public FaultInjectionStorageProvider()
        {
            realStorageProvider = new TStorage();
        }

        /// <summary>  Name of the property that controls the inserted delay. </summary>
        public const string DelayMillisecondsPropertyName = "DelayMilliseconds";

        private int delayMilliseconds;

        /// <summary>
        /// Initializes the decorated storage provider.
        /// </summary>
        /// <param name="name">Name assigned for this provider</param>
        /// <param name="providerRuntime">Callback for accessing system functions in the Provider Runtime</param>
        /// <param name="config">Configuration metadata to be used for this provider instance</param>
        /// <returns>Completion promise Task for the inttialization work for this provider</returns>
        public async Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            grainFactory = providerRuntime.GrainFactory;
            await realStorageProvider.Init(name, providerRuntime, config);
            Log = realStorageProvider.Log.GetSubLogger("FaultInjection");
            Log.Info($"Initialized fault injection for storage provider {Name}");

            string value;
            if (config.Properties.TryGetValue(DelayMillisecondsPropertyName, out value))
                delayMilliseconds = int.Parse(value);
        }

        /// <summary>Close function for this provider instance.</summary>
        /// <returns>Completion promise for the Close operation on this provider.</returns>
        public async Task Close()
        {
                await realStorageProvider.Close();
        }

        private Task InsertDelay()
        {
            if (delayMilliseconds > 0)
                return Task.Delay(delayMilliseconds);
            else
                return TaskDone.Done;
        }
           
        /// <summary>Faults if exception is provided, otherwise calls through to  decorated storage provider.</summary>
        /// <param name="grainType">Type of this grain [fully qualified class name]</param>
        /// <param name="grainReference">Grain reference object for this grain.</param>
        /// <param name="grainState">State data object to be populated for this grain.</param>
        /// <returns>Completion promise for the Read operation on the specified grain.</returns>
        public async Task ReadStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            IStorageFaultGrain faultGrain = grainFactory.GetGrain<IStorageFaultGrain>(grainType);
            try
            {
                await InsertDelay();
                await faultGrain.OnRead(grainReference);
            }
            catch (Exception)
            {
                Log.Info($"Fault injected for ReadState for grain {grainReference} of type {grainType}, ");
                throw;
            }
            Log.Info($"ReadState for grain {grainReference} of type {grainType}");
            await realStorageProvider.ReadStateAsync(grainType, grainReference, grainState);
        }

        /// <summary>Faults if exception is provided, otherwise calls through to  decorated storage provider.</summary>
        /// <param name="grainType">Type of this grain [fully qualified class name]</param>
        /// <param name="grainReference">Grain reference object for this grain.</param>
        /// <param name="grainState">State data object to be written for this grain.</param>
        /// <returns>Completion promise for the Write operation on the specified grain.</returns>
        public async Task WriteStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            IStorageFaultGrain faultGrain = grainFactory.GetGrain<IStorageFaultGrain>(grainType);
            try
            {
                await InsertDelay();
                await faultGrain.OnWrite(grainReference);
            }
            catch (Exception)
            {
                Log.Info($"Fault injected for WriteState for grain {grainReference} of type {grainType}");
                throw;
            }
            Log.Info($"WriteState for grain {grainReference} of type {grainType}");
            await realStorageProvider.WriteStateAsync(grainType, grainReference, grainState);
        }

        /// <summary>Faults if exception is provided, otherwise calls through to  decorated storage provider.</summary>
        /// <param name="grainType">Type of this grain [fully qualified class name]</param>
        /// <param name="grainReference">Grain reference object for this grain.</param>
        /// <param name="grainState">Copy of last-known state data object for this grain.</param>
        /// <returns>Completion promise for the Delete operation on the specified grain.</returns>
        public async Task ClearStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            IStorageFaultGrain faultGrain = grainFactory.GetGrain<IStorageFaultGrain>(grainType);
            try
            {
                await InsertDelay();
                await faultGrain.OnClear(grainReference);
            }
            catch (Exception)
            {
                Log.Info($"Fault injected for ClearState for grain {grainReference} of type {grainType}");
                throw;
            }
            Log.Info($"ClearState for grain {grainReference} of type {grainType}");
            await realStorageProvider.ClearStateAsync(grainType, grainReference, grainState);
        }
    }
}
