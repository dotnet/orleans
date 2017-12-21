using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    public class MemoryStorage : IStorageProvider
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
        private const string STATE_STORE_NAME = "MemoryStorage";
        private Lazy<IMemoryStorageGrain>[] storageGrains;
        private ILogger logger;
        /// <summary> Name of this storage provider instance. </summary>
        /// <see cref="IProvider.Name"/>
        public string Name { get; private set; }

        /// <summary> Default constructor. </summary>
        public MemoryStorage()
            : this(NumStorageGrainsDefaultValue)
        {
        }

        /// <summary> Constructor - use the specificed number of store grains. </summary>
        /// <param name="numStoreGrains">Number of store grains to use.</param>
        protected MemoryStorage(int numStoreGrains)
        {
            numStorageGrains = numStoreGrains;
        }

        #region IStorageProvider methods

        /// <summary> Initialization function for this storage provider. </summary>
        /// <see cref="IProvider.Init"/>
        public virtual Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            Name = name;
            var loggerName = $"{this.GetType().FullName}.{Name}";
            var loggerFactory = providerRuntime.ServiceProvider.GetRequiredService<ILoggerFactory>();
            this.logger = loggerFactory.CreateLogger(loggerName);

            string numStorageGrainsStr;
            if (config.Properties.TryGetValue(NumStorageGrainsPropertyName, out numStorageGrainsStr))
                numStorageGrains = Int32.Parse(numStorageGrainsStr);
            
            logger.Info("Init: Name={0} NumStorageGrains={1}", Name, numStorageGrains);

            storageGrains = new Lazy<IMemoryStorageGrain>[numStorageGrains];
            for (int i = 0; i < numStorageGrains; i++)
            {
                int idx = i; // Capture variable to avoid modified closure error
                storageGrains[idx] = new Lazy<IMemoryStorageGrain>(() => providerRuntime.GrainFactory.GetGrain<IMemoryStorageGrain>(idx));
            }
            return Task.CompletedTask;
        }

        /// <summary> Shutdown function for this storage provider. </summary>
        public virtual Task Close()
        {
            for (int i = 0; i < numStorageGrains; i++)
                storageGrains[i] = null;
            
            return Task.CompletedTask;
        }

        /// <summary> Read state data function for this storage provider. </summary>
        /// <see cref="IStorageProvider.ReadStateAsync"/>
        public virtual async Task ReadStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            IList<Tuple<string, string>> keys = MakeKeys(grainType, grainReference).ToList();

            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Read Keys={0}", StorageProviderUtils.PrintKeys(keys));
            
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
        /// <see cref="IStorageProvider.WriteStateAsync"/>
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
        /// <see cref="IStorageProvider.ClearStateAsync"/>
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
