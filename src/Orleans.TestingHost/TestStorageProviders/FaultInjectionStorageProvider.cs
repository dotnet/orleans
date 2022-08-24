
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Orleans.TestingHost
{
    /// <summary>
    /// Options for fault injection grain storage
    /// </summary>
    public class FaultInjectionGrainStorageOptions
    {
        /// <summary>
        /// The default latency.
        /// </summary>
        public static TimeSpan DEFAULT_LATENCY = TimeSpan.FromMilliseconds(10);

        /// <summary>
        /// Gets or sets the latency applied on storage operations.
        /// </summary>
        public TimeSpan Latency { get; set; } = DEFAULT_LATENCY;
    }

    /// <summary>
    /// Fault injection decorator for storage providers.  This allows users to inject storage exceptions to test error handling scenarios.
    /// </summary>
    public class FaultInjectionGrainStorage : IGrainStorage, ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly IGrainStorage realStorageProvider;
        private IGrainFactory grainFactory;
        private ILogger logger;
        private readonly FaultInjectionGrainStorageOptions options;

        /// <summary>
        /// Default constructor which creates the decorated storage provider.
        /// </summary>
        /// <param name="realStorageProvider">The real storage provider.</param>
        /// <param name="name">The storage provider name.</param>
        /// <param name="loggerFactory">The logger factory.</param>
        /// <param name="grainFactory">The grain factory.</param>
        /// <param name="faultInjectionOptions">The fault injection options.</param>
        public FaultInjectionGrainStorage(IGrainStorage realStorageProvider, string name, ILoggerFactory loggerFactory, 
            IGrainFactory grainFactory, FaultInjectionGrainStorageOptions faultInjectionOptions)
        {
            this.realStorageProvider = realStorageProvider;
            this.logger = loggerFactory.CreateLogger($"{this.GetType().FullName}.{name}");
            this.grainFactory = grainFactory;
            this.options = faultInjectionOptions;
        }

        private Task InsertDelay()
        {
            return Task.Delay(this.options.Latency);
        }
           
        /// <summary>Faults if exception is provided, otherwise calls through to  decorated storage provider.</summary>
        /// <returns>Completion promise for the Read operation on the specified grain.</returns>
        public async Task ReadStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            IStorageFaultGrain faultGrain = grainFactory.GetGrain<IStorageFaultGrain>(grainType);
            try
            {
                await InsertDelay();
                await faultGrain.OnRead(grainId);
            }
            catch (Exception)
            {
                logger.LogInformation(
                    "Fault injected for ReadState for grain {GrainId} of type {GrainType}",
                    grainId,
                    grainType);
                throw;
            }
            logger.LogInformation(
                "ReadState for grain {GrainId} of type {GrainType}",
                grainId,
                grainType);
            await realStorageProvider.ReadStateAsync(grainType, grainId, grainState);
        }

        /// <summary>Faults if exception is provided, otherwise calls through to  decorated storage provider.</summary>
        /// <returns>Completion promise for the Write operation on the specified grain.</returns>
        public async Task WriteStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            IStorageFaultGrain faultGrain = grainFactory.GetGrain<IStorageFaultGrain>(grainType);
            try
            {
                await InsertDelay();
                await faultGrain.OnWrite(grainId);
            }
            catch (Exception)
            {
                logger.LogInformation(
                    "Fault injected for WriteState for grain {GrainId} of type {GrainType}",
                    grainId,
                    grainType);
                throw;
            }
            logger.LogInformation(
                "WriteState for grain {GrainId} of type {GrainType}",
                grainId,
                grainType);
            await realStorageProvider.WriteStateAsync(grainType, grainId, grainState);
        }

        /// <summary>Faults if exception is provided, otherwise calls through to  decorated storage provider.</summary>
        /// <returns>Completion promise for the Delete operation on the specified grain.</returns>
        public async Task ClearStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            IStorageFaultGrain faultGrain = grainFactory.GetGrain<IStorageFaultGrain>(grainType);
            try
            {
                await InsertDelay();
                await faultGrain.OnClear(grainId);
            }
            catch (Exception)
            {
                logger.LogInformation(
                    "Fault injected for ClearState for grain {GrainId} of type {GrainType}",
                    grainId,
                    grainType);
                throw;
            }

            logger.LogInformation(
                "ClearState for grain {GrainId} of type {GrainType}",
                grainId,
                grainType);
            await realStorageProvider.ClearStateAsync(grainType, grainId, grainState);
        }

        /// <inheritdoc />
        public void Participate(ISiloLifecycle lifecycle)
        {
            (realStorageProvider as ILifecycleParticipant<ISiloLifecycle>)?.Participate(lifecycle);
        }
    }

    /// <summary>
    /// Factory to create FaultInjectionGrainStorage
    /// </summary>
    public static class FaultInjectionGrainStorageFactory
    {
        /// <summary>
        /// Creates a new <see cref="FaultInjectionGrainStorage"/> instance.
        /// </summary>
        /// <param name="services">The services.</param>
        /// <param name="name">The storage provider name.</param>
        /// <param name="injectedGrainStorageFactory">The injected grain storage factory.</param>
        /// <returns>The new instance.</returns>
        public static IGrainStorage Create(IServiceProvider services, string name, Func<IServiceProvider, string, IGrainStorage> injectedGrainStorageFactory)
        {
            return new FaultInjectionGrainStorage(injectedGrainStorageFactory(services,name), name, services.GetRequiredService<ILoggerFactory>(), services.GetRequiredService<IGrainFactory>(),
                services.GetRequiredService<IOptionsMonitor<FaultInjectionGrainStorageOptions>>().Get(name));
        }
    }
}
