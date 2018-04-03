using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers;
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
    [DebuggerDisplay("MemoryStore:{Name}")]
    public class MemoryGrainStorage : IGrainStorage, IDisposable
    {
        private MemoryGrainStorageOptions options;
        private const string STATE_STORE_NAME = "MemoryStorage";
        private Lazy<IMemoryStorageGrain>[] storageGrains;
        private ILogger logger;
        private IGrainFactory grainFactory;

        /// <summary> Name of this storage provider instance. </summary>
        private readonly string name;

        /// <summary> Default constructor. </summary>
        public MemoryGrainStorage(string name, MemoryGrainStorageOptions options, ILoggerFactory loggerFactory, IGrainFactory grainFactory)
        {
            this.options = options;
            this.name = name;
            this.logger = loggerFactory.CreateLogger($"{this.GetType().FullName}.{name}");
            this.grainFactory = grainFactory;

            //Init
            logger.Info("Init: Name={0} NumStorageGrains={1}", name, this.options.NumStorageGrains);

            storageGrains = new Lazy<IMemoryStorageGrain>[this.options.NumStorageGrains];
            for (int i = 0; i < this.options.NumStorageGrains; i++)
            {
                int idx = i; // Capture variable to avoid modified closure error
                storageGrains[idx] = new Lazy<IMemoryStorageGrain>(() => this.grainFactory.GetGrain<IMemoryStorageGrain>(idx));
            }
        }

        /// <summary> Read state data function for this storage provider. </summary>
        /// <see cref="IGrainStorage.ReadStateAsync"/>
        public virtual async Task ReadStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            IList<Tuple<string, string>> keys = MakeKeys(grainType, grainReference).ToList();

            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Read Keys={0}", StorageProviderUtils.PrintKeys(keys));
            
            string id = HierarchicalKeyStore.MakeStoreKey(keys);
            IMemoryStorageGrain storageGrain = GetStorageGrain(id);
            var state = await storageGrain.ReadStateAsync(STATE_STORE_NAME, id);
            if (state != null)
            {
                grainState.ETag = state.ETag;
                grainState.State = state.State;
            }
        }

        /// <summary> Write state data function for this storage provider. </summary>
        /// <see cref="IGrainStorage.WriteStateAsync"/>
        public virtual async Task WriteStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            IList<Tuple<string, string>> keys = MakeKeys(grainType, grainReference).ToList();
            string key = HierarchicalKeyStore.MakeStoreKey(keys);
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Write {0} ", StorageProviderUtils.PrintOneWrite(keys, grainState.State, grainState.ETag));
            IMemoryStorageGrain storageGrain = GetStorageGrain(key);
            try
            {
                grainState.ETag = await storageGrain.WriteStateAsync(STATE_STORE_NAME, key, grainState);
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
            IList<Tuple<string, string>> keys = MakeKeys(grainType, grainReference).ToList();
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Delete Keys={0} Etag={1}", StorageProviderUtils.PrintKeys(keys), grainState.ETag);
            string key = HierarchicalKeyStore.MakeStoreKey(keys);
            IMemoryStorageGrain storageGrain = GetStorageGrain(key);
            try
            {
                await storageGrain.DeleteStateAsync(STATE_STORE_NAME, key, grainState.ETag);
                grainState.ETag = null;
            }
            catch (MemoryStorageEtagMismatchException e)
            {
                throw e.AsInconsistentStateException();
            }
        }

        private static IEnumerable<Tuple<string, string>> MakeKeys(string grainType, GrainReference grain)
        {
            return new[]
            {
                Tuple.Create("GrainType", grainType),
                Tuple.Create("GrainId", grain.ToKeyString())
            };
        }

        private IMemoryStorageGrain GetStorageGrain(string id)
        {
            int idx = StorageProviderUtils.PositiveHash(id.GetHashCode(), this.options.NumStorageGrains);
            IMemoryStorageGrain storageGrain = storageGrains[idx].Value;
            return storageGrain;
        }

        internal static Func<IDictionary<string, object>, bool> GetComparer<T>(string rangeParamName, T fromValue, T toValue) where T : IComparable
        {
            Comparer comparer = Comparer.DefaultInvariant;
            bool sameRange = comparer.Compare(fromValue, toValue) == 0; // FromValue == ToValue
            bool insideRange = comparer.Compare(fromValue, toValue) < 0; // FromValue < ToValue
            Func<IDictionary<string, object>, bool> compareClause;
            if (sameRange)
            {
                compareClause = data =>
                {
                    if (data == null || data.Count <= 0) return false;

                    if (!data.ContainsKey(rangeParamName))
                    {
                        var error = string.Format("Cannot find column '{0}' for range query from {1} to {2} in Data={3}",
                            rangeParamName, fromValue, toValue, StorageProviderUtils.PrintData(data));
                        throw new KeyNotFoundException(error);
                    }
                    T obj = (T) data[rangeParamName];
                    return comparer.Compare(obj, fromValue) == 0;
                };
            }
            else if (insideRange)
            {
                compareClause = data =>
                {
                    if (data == null || data.Count <= 0) return false;

                    if (!data.ContainsKey(rangeParamName))
                    {
                        var error = string.Format("Cannot find column '{0}' for range query from {1} to {2} in Data={3}",
                            rangeParamName, fromValue, toValue, StorageProviderUtils.PrintData(data));
                        throw new KeyNotFoundException(error);
                    }
                    T obj = (T) data[rangeParamName];
                    return comparer.Compare(obj, fromValue) >= 0 && comparer.Compare(obj, toValue) <= 0;
                };
            }
            else
            {
                compareClause = data =>
                {
                    if (data == null || data.Count <= 0) return false;

                    if (!data.ContainsKey(rangeParamName))
                    {
                        var error = string.Format("Cannot find column '{0}' for range query from {1} to {2} in Data={3}",
                            rangeParamName, fromValue, toValue, StorageProviderUtils.PrintData(data));
                        throw new KeyNotFoundException(error);
                    }
                    T obj = (T) data[rangeParamName];
                    return comparer.Compare(obj, fromValue) >= 0 || comparer.Compare(obj, toValue) <= 0;
                };
            }
            return compareClause;
        }

        public void Dispose()
        {
            for (int i = 0; i < this.options.NumStorageGrains; i++)
                storageGrains[i] = null;
        }
    }

    /// <summary>
    /// Factory for creating MemoryGrainStorage
    /// </summary>
    public class MemoryGrainStorageFactory
    {
        public static IGrainStorage Create(IServiceProvider services, string name)
        {
            return ActivatorUtilities.CreateInstance<MemoryGrainStorage>(services,
                services.GetRequiredService<IOptionsSnapshot<MemoryGrainStorageOptions>>().Get(name), name);
        }
    }
}
