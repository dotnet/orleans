using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Azure.SqlDatabase.ElasticScale.ShardManagement;
using Orleans.Runtime;

namespace Orleans.SqlUtils.StorageProvider
{
    /// <summary>
    /// Main class which is used by the actual Orleans SqlStorageProvider
    /// Decoupled from the provider for easy testability and avoidance of Orleans dependency
    /// </summary>
    internal class SqlDataManager : IDisposable
    {
        private readonly Logger Logger;

        private readonly RangeShardMap<int> _shardMap;
        private readonly ConcurrentDictionary<Range<int>, ShardBatcher> _shardBatchers;

        public SqlDataManager(
            Logger logger,
            GrainStateMap grainStateMap, 
            string connectionString, 
            string shardCredentials, 
            string mapName,
            BatchingOptions batchingOptions =null)
        {
            Logger = logger;
            Guard.NotNullOrEmpty(connectionString, "conectionString");
            Guard.NotNullOrEmpty(shardCredentials, "shardCredentials");
            Guard.NotNullOrEmpty(mapName, "mapName");

            // Try to get a reference to the Shard Map Manager via the Shard Map Manager database.  
            // If it doesn't already exist, then fail 
            var shardMapManager = ShardMapManagerFactory.GetSqlShardMapManager(connectionString, ShardMapManagerLoadPolicy.Lazy);
            var shardMap = (RangeShardMap<int>)shardMapManager.GetShardMap(mapName);
            var shardBatchers = new ConcurrentDictionary<Range<int>, ShardBatcher>();
            foreach (var rangeMapping in shardMap.GetMappings())
            {
                Range<int> range = rangeMapping.Value;
                shardBatchers.TryAdd(range, new ShardBatcher(logger, grainStateMap, rangeMapping.Shard, shardCredentials, batchingOptions));
            }

            _shardBatchers = shardBatchers;
            _shardMap = shardMap;
        }

        public void Dispose()
        {
            foreach (var sb in _shardBatchers)
                sb.Value.Dispose();
            _shardBatchers.Clear();
        }

        public async Task<object> ReadStateAsync(GrainIdentity grainIdentity)
        {
            Guard.NotNull(grainIdentity, "grainIdentity");

            ShardBatcher shardBatcher = LookupShardBatcher(grainIdentity);

            // We don't want to measure elapsed in case of exception
            var sw = Stopwatch.StartNew();
            var state = await shardBatcher.ReadStateAsync(grainIdentity);
            Logger.Info("ReadStateAsync for {1} elapsed {0}", sw.Elapsed, grainIdentity.GrainType);
            return state;
        }

        public async Task UpsertStateAsync(GrainIdentity grainIdentity, object state)
        {
            Guard.NotNull(grainIdentity, "grainIdentity");
            Guard.NotNull(state, "state");

            ShardBatcher shardBatcher = LookupShardBatcher(grainIdentity);

            // We don't want to measure elapsed in case of exception
            var sw = Stopwatch.StartNew();
            await shardBatcher.UpsertStateAsync(grainIdentity, state);
            Logger.Info("UpsertStateAsync for {1} elapsed {0}", sw.Elapsed, grainIdentity.GrainType);
        }

        private ShardBatcher LookupShardBatcher(GrainIdentity grainIdentity)
        {
            Range<int> range = Lookup2(grainIdentity);
            ShardBatcher shardBatcher;
            if (!_shardBatchers.TryGetValue(range, out shardBatcher))
                throw new ArgumentOutOfRangeException(string.Format("No batcher found for shard key {0}", grainIdentity.ShardKey));
            return shardBatcher;
        }


        /// <summary>
        /// Old version of Lookup which leads to hitting sql map db per every request
        /// which slows down the whole performance
        /// </summary>
        /// <param name="grainIdentity"></param>
        /// <returns></returns>
        private Range<int> Lookup1(GrainIdentity grainIdentity)
        {
            RangeMapping<int> rangeMapping = _shardMap.GetMappingForKey(grainIdentity.ShardKey);
            return rangeMapping.Value;
        }

        /// <summary>
        /// Fix for Lookup1. Uses a cached version of ranges
        /// </summary>
        /// <param name="grainIdentity"></param>
        /// <returns></returns>
        private Range<int> Lookup2(GrainIdentity grainIdentity)
        {
            var shardKey = grainIdentity.ShardKey;
            return _shardBatchers.Keys
                .FirstOrDefault(range => range.Low <= shardKey && (shardKey < range.High || range.HighIsMax));
        }
    }
}