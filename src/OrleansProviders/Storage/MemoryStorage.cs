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
        private const string STATE_STORE_NAME = "MemoryStorage";
        private Lazy<IMemoryStorageGrain>[] storageGrains;
        private readonly ILogger logger;

        /// <summary> Name of this storage provider instance. </summary>
        private readonly string name;

        /// <summary> Default constructor. </summary>
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

        /// <summary> Read state data function for this storage provider. </summary>
        /// <see cref="IGrainStorage.ReadStateAsync"/>
        public virtual async Task ReadStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            var keys = MakeKeys(grainType, grainReference);

            if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace("Read Keys={Keys}", StorageProviderUtils.PrintKeys(keys));

            string id = HierarchicalKeyStore.MakeStoreKey(keys);
            IMemoryStorageGrain storageGrain = GetStorageGrain(id);
            var state = await storageGrain.ReadStateAsync(STATE_STORE_NAME, id);
            if (state != null)
            {
                grainState.ETag = state.ETag;
                grainState.State = state.State;
                grainState.RecordExists = true;
            }
        }

        /// <summary> Write state data function for this storage provider. </summary>
        /// <see cref="IGrainStorage.WriteStateAsync"/>
        public virtual async Task WriteStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            var keys = MakeKeys(grainType, grainReference);
            string key = HierarchicalKeyStore.MakeStoreKey(keys);
            if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace("Write {Write} ", StorageProviderUtils.PrintOneWrite(keys, grainState.State, grainState.ETag));
            IMemoryStorageGrain storageGrain = GetStorageGrain(key);
            try
            {
                grainState.ETag = await storageGrain.WriteStateAsync(STATE_STORE_NAME, key, grainState);
                grainState.RecordExists = true;
            }
            catch (MemoryStorageEtagMismatchException e)
            {
                throw e.AsInconsistentStateException();
            }
        }

        /// <summary> Delete / Clear state data function for this storage provider. </summary>
        /// <see cref="IGrainStorage.ClearStateAsync"/>
        public virtual async Task ClearStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            var keys = MakeKeys(grainType, grainReference);
            if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace("Delete Keys={Keys} Etag={Etag}", StorageProviderUtils.PrintKeys(keys), grainState.ETag);
            string key = HierarchicalKeyStore.MakeStoreKey(keys);
            IMemoryStorageGrain storageGrain = GetStorageGrain(key);
            try
            {
                await storageGrain.DeleteStateAsync(STATE_STORE_NAME, key, grainState.ETag);
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

        public void Dispose() => storageGrains = null;
    }

    /// <summary>
    /// Factory for creating MemoryGrainStorage
    /// </summary>
    public static class MemoryGrainStorageFactory
    {
        public static IGrainStorage Create(IServiceProvider services, string name)
        {
            return ActivatorUtilities.CreateInstance<MemoryGrainStorage>(services,
                services.GetRequiredService<IOptionsMonitor<MemoryGrainStorageOptions>>().Get(name), name);
        }
    }
}
