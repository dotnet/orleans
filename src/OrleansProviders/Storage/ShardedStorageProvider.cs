using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Orleans.Runtime;
using Orleans.Providers;

namespace Orleans.Storage
{
    /// <summary>
    /// Simple storage provider for writing grain state data shared across a number of other storage providers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Required nested configuration elements: 
    /// <c>Provider</c> -- References by-name to other provider instances defined elsewheer in thios configuration file.
    /// </para>
    /// <para>
    /// A consistent hash functions (default is Jenkins Hash) is used to decide which
    /// shard (in the order they are defined in the config file) is responsible for storing 
    /// state data for a specified grain, then the Read / Write / Clear request 
    /// is bridged over to the appropriate underlying provider for execution.
    /// </para>
    /// <para>
    /// <see cref="http://en.wikipedia.org/wiki/Jenkins_hash"/> for more information 
    /// about the Jenkins Hash function.
    /// </para>
    /// </remarks>
    /// <example>
    /// Example configuration for this storage provider in OrleansConfiguration.xml file:
    /// <code>
    /// &lt;OrleansConfiguration xmlns="urn:orleans">
    ///   &lt;Globals>
    ///     &lt;StorageProviders>
    ///       &lt;Provider Type="Orleans.Storage.AzureTableStorage" Name="AzureStore1" DataConnectionString="..." />
    ///       &lt;Provider Type="Orleans.Storage.AzureTableStorage" Name="AzureStore2" DataConnectionString="..." />
    ///       &lt;Provider Type="Orleans.Storage.ShardedStorageProvider" Name="ShardedAzureStore">
    ///         &lt;Provider Name="AzureStore1"/>
    ///         &lt;Provider Name="AzureStore2"/>
    ///       &lt;/Provider>
    ///     &lt;/StorageProviders>
    /// </code>
    /// </example>
    public class ShardedStorageProvider : IStorageProvider
    {
        private IStorageProvider[] storageProviders;
        private static int counter;
        private readonly int id;

        /// <summary>
        /// Default constructor.
        /// </summary>
        public ShardedStorageProvider()
        {
            id = Interlocked.Increment(ref counter);
        }

        /// <summary> Name of this storage provider instance. </summary>
        /// <see cref="IProvider#Name"/>
        public string Name { get; private set; }

        /// <summary> Logger used by this storage provider instance. </summary>
        /// <see cref="IStorageProvider#Log"/>
        public Logger Log { get; private set; }

        /// <summary>
        /// Return a hash value derived from the input grain type and id values.
        /// </summary>
        /// <param name="grainType">Fully qualified class type name for this grain</param>
        /// <param name="grainReference">GrainI reference for this grain</param>
        /// <returns>Stable hash value for this grain</returns>
        protected virtual int HashFunction(string grainType, GrainReference grainReference)
        {
            return StorageProviderUtils.PositiveHash(grainReference, storageProviders.Length);
        }

        /// <summary> Initialization function for this storage provider. </summary>
        /// <see cref="IProvider#Init"/>
        public Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            Name = name;
            // Provider #1 & #2 are required, #3 - #9 are optional
            if(config.Children.Count == 0)
                throw new ArgumentException("No provider is defined.");
            if(config.Children.Count == 1)
                throw new ArgumentException("At least two providers have to be listed.");

            Log = providerRuntime.GetLogger("Storage.ShardedStorageProvider." + id);

            var providers = new List<IStorageProvider>();
            int index = 0;
            foreach( var provider in config.Children)
            {
                Log.Info((int)ProviderErrorCode.ShardedStorageProvider_ProviderName, "Provider {0} = {1}", index++, provider.Name);
                providers.Add((IStorageProvider)provider);
                
            }
            storageProviders = providers.ToArray();
            // Storage providers will already have been initialized by the provider manager, so we don't need to orchestrate that
            return TaskDone.Done;
        }

        /// <summary> Shutdown function for this storage provider. </summary>
        /// <see cref="IStorageProvider#Close"/>
        public Task Close()
        {
            var closeTasks = new List<Task>();
            foreach (var provider in storageProviders)
                closeTasks.Add(provider.Close());
            
            return Task.WhenAll(closeTasks);
        }

        /// <summary> Read state data function for this storage provider. </summary>
        /// <see cref="IStorageProvider#ReadStateAsync"/>
        public Task ReadStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            int num = FindStorageShard(grainType, grainReference);
            IStorageProvider provider = storageProviders[num];
            return provider.ReadStateAsync(grainType, grainReference, grainState);
        }

        /// <summary> Write state data function for this storage provider. </summary>
        /// <see cref="IStorageProvider#WriteStateAsync"/>
        public Task WriteStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            int num = FindStorageShard(grainType, grainReference);
            IStorageProvider provider = storageProviders[num];
            return provider.WriteStateAsync(grainType, grainReference, grainState);
        }

        /// <summary> Deleet / Clear state data function for this storage provider. </summary>
        /// <see cref="IStorageProvider#ClearStateAsync"/>
        public Task ClearStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            int num = FindStorageShard(grainType, grainReference);
            IStorageProvider provider = storageProviders[num];
            return provider.ClearStateAsync(grainType, grainReference, grainState);
        }

        private int FindStorageShard(string grainType, GrainReference grainReference)
        {
            int num = HashFunction(grainType, grainReference);
            if (num < 0 || num >= storageProviders.Length)
            {
                var msg = String.Format("Hash function returned out of bounds value {0}. This is an error.", num);
                Log.Error((int)ProviderErrorCode.ShardedStorageProvider_HashValueOutOfBounds, msg);
                throw new OrleansException(msg);
            }
            return num;
        }
    }
}
