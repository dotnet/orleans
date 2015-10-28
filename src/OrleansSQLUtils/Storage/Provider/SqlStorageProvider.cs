using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.SqlUtils.StorageProvider
{
    public class SqlStorageProvider : IStorageProvider
    {
        /// <summary>
        /// logger object
        /// </summary>
        public Logger Log { get; private set; }

        /// <summary>
        /// Storage provider name
        /// </summary>
        public string Name { get; private set; }

        public string ConnectionString { get; private set; }

        private SqlDataManager _dataManager;

        // Ignore usage of SqlDataManager - used only for testing
        private bool _ignore;

        /// <summary>
        /// Initializes the storage provider.
        /// </summary>
        /// <param name="name">The name of this provider instance.</param>
        /// <param name="providerRuntime">A Orleans runtime object managing all storage providers.</param>
        /// <param name="config">Configuration info for this provider instance.</param>
        /// <returns>Completion promise for this operation.</returns>
        public virtual Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            // ASSERT Log != null
            Log = providerRuntime.GetLogger(this.GetType().FullName);
            
            Log.Info("Init {0}", name);

            Name = name;
            ConnectionString = config.GetProperty("ConnectionString"); 
            string mapName = config.GetProperty("MapName");
            string shardCredentials = config.GetProperty("ShardCredentials");
            string factoryTypeName = config.GetProperty("StateMapFactoryType");
            _ignore = config.GetPropertyBool("Ignore", false);

            if (_ignore)
                Log.Info("!!!Actual SQL persistance will be ignored!!!");

            // Look for a specified StateMapFactoryType or the first type implementing IGrainStateMapFactory
            Type factoryType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var type in asm.GetTypes())
                {
                    if (type != null &&
                        (!string.IsNullOrEmpty(factoryTypeName) && type.FullName == factoryTypeName ||
                         string.IsNullOrEmpty(factoryTypeName) &&
                         null != type.GetInterface(typeof (IGrainStateMapFactory).FullName)))
                    {
                        factoryType = type;
                        break;
                    }
                }
            }
            if (null == factoryType)
                throw new ArgumentException(string.Format("Could not locate a state map factory type {0}", factoryTypeName));

            var factory = (IGrainStateMapFactory)Activator.CreateInstance(factoryType);
            var grainStateMap = factory.CreateGrainStateMap();
            _dataManager = new SqlDataManager(providerRuntime.GetLogger("SqlDataManager"), grainStateMap, ConnectionString, shardCredentials, mapName);
            return TaskDone.Done;
        }

        public Task Close()
        {
            Log.Info("Close");
            
            _dataManager.Dispose();

            return TaskDone.Done;
        }

        public async Task ReadStateAsync(string grainType, GrainReference grainReference, GrainState grainState)
        {
            var grainIdentity = GrainIdentity.FromGrainReference(grainType, grainReference);

            if (_ignore)
                return;

            var state = await _dataManager.ReadStateAsync(grainIdentity);
            if (null != state)
                grainState.SetAll(state);
        }

        public async Task WriteStateAsync(string grainType, GrainReference grainReference, GrainState grainState)
        {
            if (_ignore)
                return;

            var grainIdentity = GrainIdentity.FromGrainReference(grainType, grainReference);
            await _dataManager.UpsertStateAsync(grainIdentity, grainState.AsDictionary());
        }


        /// <summary>
        /// TODO Not implemented
        /// </summary>
        /// <param name="grainType"></param>
        /// <param name="grainReference"></param>
        /// <param name="grainState"></param>
        /// <returns></returns>
        public Task ClearStateAsync(string grainType, GrainReference grainReference, GrainState grainState)
        {
            Log.Verbose2("ClearStateAsync {0} {1} {2}", grainType, grainReference.ToKeyString(), grainState.Etag);

            return TaskDone.Done;
        }
    }
}
