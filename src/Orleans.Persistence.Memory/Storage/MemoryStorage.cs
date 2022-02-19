using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Storage.Internal;

namespace Orleans.Storage
{

    /// <summary>
    /// This is a simple in-memory grain implementation of a storage provider.
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
    ///     &lt;StorageProviders>
    ///       &lt;Provider Type="Orleans.Storage.MemoryStorage" Name="MemoryStore" />
    ///   &lt;/StorageProviders>
    /// </code>
    /// </example>
    [DebuggerDisplay("MemoryStore:{" + nameof(name) + "}")]
    public class MemoryGrainStorage : IGrainStorage, IDisposable
    {
        private Lazy<IMemoryStorageGrain>[] storageGrains;
        private readonly ILogger logger;

        /// <summary> Name of this storage provider instance. </summary>
        private readonly string name;

        /// <summary>
        /// Initializes a new instance of the <see cref="MemoryGrainStorage"/> class.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <param name="options">The options.</param>
        /// <param name="logger">The logger.</param>
        /// <param name="grainFactory">The grain factory.</param>
        public MemoryGrainStorage(string name, MemoryGrainStorageOptions options, ILogger<MemoryGrainStorage> logger, IGrainFactory grainFactory)
        {
            this.name = name;
            this.logger = logger;

            //Init
            logger.LogInformation("Init: Name={Name} NumStorageGrains={NumStorageGrains}", name, options.NumStorageGrains);

            storageGrains = new Lazy<IMemoryStorageGrain>[options.NumStorageGrains];
            for (int i = 0; i < storageGrains.Length; i++)
            {
                int idx = i; // Capture variable to avoid modified closure error
                storageGrains[idx] = new Lazy<IMemoryStorageGrain>(() => grainFactory.GetGrain<IMemoryStorageGrain>(idx));
            }
        }

        /// <inheritdoc/>
        public virtual async Task ReadStateAsync<T>(string grainType, GrainReference grainReference, IGrainState<T> grainState)
        {
            var keys = MakeKeys(grainType, grainReference);

            if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace("Read Keys={Keys}", StorageProviderUtils.PrintKeys(keys));

            string id = HierarchicalKeyStore.MakeStoreKey(keys);
            IMemoryStorageGrain storageGrain = GetStorageGrain(id);
            var state = await storageGrain.ReadStateAsync<T>(id);
            if (state != null)
            {
                grainState.ETag = state.ETag;
                grainState.State = (T)state.State;
                grainState.RecordExists = true;
            }
        }

        /// <inheritdoc/>
        public virtual async Task WriteStateAsync<T>(string grainType, GrainReference grainReference, IGrainState<T> grainState)
        {
            var keys = MakeKeys(grainType, grainReference);
            string key = HierarchicalKeyStore.MakeStoreKey(keys);
            if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace("Write {Write} ", StorageProviderUtils.PrintOneWrite(keys, grainState.State, grainState.ETag));
            IMemoryStorageGrain storageGrain = GetStorageGrain(key);
            try
            {
                grainState.ETag = await storageGrain.WriteStateAsync(key, grainState);
                grainState.RecordExists = true;
            }
            catch (MemoryStorageEtagMismatchException e)
            {
                throw e.AsInconsistentStateException();
            }
        }

        /// <inheritdoc/>
        public virtual async Task ClearStateAsync<T>(string grainType, GrainReference grainReference, IGrainState<T> grainState)
        {
            var keys = MakeKeys(grainType, grainReference);
            if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace("Delete Keys={Keys} Etag={Etag}", StorageProviderUtils.PrintKeys(keys), grainState.ETag);
            string key = HierarchicalKeyStore.MakeStoreKey(keys);
            IMemoryStorageGrain storageGrain = GetStorageGrain(key);
            try
            {
                await storageGrain.DeleteStateAsync<T>(key, grainState.ETag);
                grainState.ETag = null;
                grainState.RecordExists = false;
            }
            catch (MemoryStorageEtagMismatchException e)
            {
                throw e.AsInconsistentStateException();
            }
        }

        private static Tuple<string, string>[] MakeKeys(string grainType, GrainReference grain)
        {
            return new[]
            {
                Tuple.Create("GrainType", grainType),
                Tuple.Create("GrainId", grain.GrainId.ToString())
            };
        }

        private IMemoryStorageGrain GetStorageGrain(string id)
        {
            int idx = StorageProviderUtils.PositiveHash(id.GetHashCode(), this.storageGrains.Length);
            IMemoryStorageGrain storageGrain = storageGrains[idx].Value;
            return storageGrain;
        }

        /// <inheritdoc/>
        public void Dispose() => storageGrains = null;
    }

    /// <summary>
    /// Factory for creating MemoryGrainStorage
    /// </summary>
    public static class MemoryGrainStorageFactory
    {
        /// <summary>
        /// Creates a new <see cref="MemoryGrainStorage"/> instance.
        /// </summary>
        /// <param name="services">The services.</param>
        /// <param name="name">The name.</param>
        /// <returns>The storage.</returns>
        public static IGrainStorage Create(IServiceProvider services, string name)
        {
            return ActivatorUtilities.CreateInstance<MemoryGrainStorage>(services,
                services.GetRequiredService<IOptionsMonitor<MemoryGrainStorageOptions>>().Get(name), name);
        }
    }
}
