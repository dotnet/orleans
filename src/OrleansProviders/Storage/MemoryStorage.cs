using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Orleans.Runtime;
using Orleans.Providers;

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
    public class MemoryStorage : IStorageProvider
    {
        private const int DEFAULT_NUM_STORAGE_GRAINS = 10;
        private const string NUM_STORAGE_GRAINS = "NumStorageGrains";
        private int numStorageGrains;
        private static int counter;
        private readonly int id;
        private const string STATE_STORE_NAME = "MemoryStorage";
        private Lazy<IMemoryStorageGrain>[] storageGrains;

        /// <summary> Name of this storage provider instance. </summary>
        /// <see cref="IProvider#Name"/>
        public string Name { get; private set; }

        /// <summary> Logger used by this storage provider instance. </summary>
        /// <see cref="IStorageProvider#Log"/>
        public Logger Log { get; private set; }

        public MemoryStorage()
            : this(DEFAULT_NUM_STORAGE_GRAINS)
        {
        }

        protected MemoryStorage(int numStoreGrains)
        {
            id = Interlocked.Increment(ref counter);
            numStorageGrains = numStoreGrains;
        }

        protected virtual string GetLoggerName()
        {
            return string.Format("Storage.{0}.{1}", GetType().Name, id);
        }

        #region IStorageProvider methods

        /// <summary> Initialization function for this storage provider. </summary>
        /// <see cref="IProvider#Init"/>
        public virtual Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            Name = name;
            Log = providerRuntime.GetLogger(GetLoggerName());

            string numStorageGrainsStr;
            if (config.Properties.TryGetValue(NUM_STORAGE_GRAINS, out numStorageGrainsStr))
                numStorageGrains = Int32.Parse(numStorageGrainsStr);
            
            Log.Info("Init: Name={0} NumStorageGrains={1}", Name, numStorageGrains);

            storageGrains = new Lazy<IMemoryStorageGrain>[numStorageGrains];
            for (int i = 0; i < numStorageGrains; i++)
            {
                int idx = i; // Capture variable to avoid modified closure error
                storageGrains[idx] = new Lazy<IMemoryStorageGrain>(() => providerRuntime.GrainFactory.GetGrain<IMemoryStorageGrain>(idx));
            }
            return TaskDone.Done;
        }

        /// <summary> Shutdown function for this storage provider. </summary>
        /// <see cref="IStorageProvider#Close"/>
        public virtual Task Close()
        {
            for (int i = 0; i < numStorageGrains; i++)
                storageGrains[i] = null;
            
            return TaskDone.Done;
        }

        /// <summary> Read state data function for this storage provider. </summary>
        /// <see cref="IStorageProvider#ReadStateAsync"/>
        public virtual async Task ReadStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            var keys = MakeKeys(grainType, grainReference);

            if (Log.IsVerbose2) Log.Verbose2("Read Keys={0}", StorageProviderUtils.PrintKeys(keys));
            
            string id = HierarchicalKeyStore.MakeStoreKey(keys);
            IMemoryStorageGrain storageGrain = GetStorageGrain(id);
            var state = await storageGrain.ReadStateAsync(STATE_STORE_NAME, id);
            if (state != null && state.State != null)
            {
                grainState.ETag = state.ETag;
                grainState.State = state.State;
            }
        }

        /// <summary> Write state data function for this storage provider. </summary>
        /// <see cref="IStorageProvider#WriteStateAsync"/>
        public virtual async Task WriteStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            var keys = MakeKeys(grainType, grainReference);
            string key = HierarchicalKeyStore.MakeStoreKey(keys);
            if (Log.IsVerbose2) Log.Verbose2("Write {0} ", StorageProviderUtils.PrintOneWrite(keys, grainState.State, grainState.ETag));
            IMemoryStorageGrain storageGrain = GetStorageGrain(key);
            grainState.ETag = await storageGrain.WriteStateAsync(STATE_STORE_NAME, key, grainState);
        }

        /// <summary> Delete / Clear state data function for this storage provider. </summary>
        /// <see cref="IStorageProvider#ClearStateAsync"/>
        public virtual async Task ClearStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            var keys = MakeKeys(grainType, grainReference);
            if (Log.IsVerbose2) Log.Verbose2("Delete Keys={0} Etag={1}", StorageProviderUtils.PrintKeys(keys), grainState.ETag);
            string key = HierarchicalKeyStore.MakeStoreKey(keys);
            IMemoryStorageGrain storageGrain = GetStorageGrain(key);
            await storageGrain.DeleteStateAsync(STATE_STORE_NAME, key, grainState.ETag);
        }

        #endregion

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
            int idx = StorageProviderUtils.PositiveHash(id.GetHashCode(), numStorageGrains);
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
    }
}
