using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Runtime.Providers;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime
{
    internal class TypeManager : SystemTarget, IClusterTypeManager, ISiloTypeManager
    {
        private readonly Logger logger = LogManager.GetLogger("TypeManager");
        private readonly GrainTypeManager grainTypeManager;
        private readonly ISiloStatusOracle statusOracle;
        private readonly OrleansTaskScheduler scheduler;
        private readonly AsyncTaskSafeTimer refreshClusterGrainInterfaceMapTimer;

        internal TypeManager(SiloAddress myAddr, GrainTypeManager grainTypeManager, ISiloStatusOracle oracle, OrleansTaskScheduler scheduler)
            : base(Constants.TypeManagerId, myAddr)
        {
            if (grainTypeManager == null)
                throw new ArgumentNullException(nameof(grainTypeManager));
            if (oracle == null)
                throw new ArgumentNullException(nameof(oracle));
            if (scheduler == null)
                throw new ArgumentNullException(nameof(scheduler));

            this.grainTypeManager = grainTypeManager;
            statusOracle = oracle;
            this.scheduler = scheduler;
            this.refreshClusterGrainInterfaceMapTimer = new AsyncTaskSafeTimer(
                    OnRefreshClusterMapTimer,
                    null,
                    TimeSpan.Zero,  // Force to do it once right now
                    TimeSpan.FromMilliseconds(500)); // TODO Config
        }


        public Task<IGrainTypeResolver> GetClusterTypeCodeMap()
        {
            return Task.FromResult<IGrainTypeResolver>(grainTypeManager.ClusterGrainInterfaceMap);
        }

        public Task<GrainInterfaceMap> GetSiloTypeCodeMap()
        {
            return Task.FromResult(grainTypeManager.GetTypeCodeMap());
        }

        public Task<Streams.ImplicitStreamSubscriberTable> GetImplicitStreamSubscriberTable(SiloAddress silo)
        {
            Streams.ImplicitStreamSubscriberTable table = SiloProviderRuntime.Instance.ImplicitStreamSubscriberTable;
            if (null == table)
            {
                throw new InvalidOperationException("the implicit stream subscriber table is not initialized");
            }
            return Task.FromResult(table);
        }

        private async Task OnRefreshClusterMapTimer(object _)
        {

            logger.Info("OnRefreshClusterMapTimer: refresh start");
            var activeSilos = statusOracle.GetApproximateSiloStatuses(onlyActive: true);
            var knownSilosClusterGrainInterfaceMap = grainTypeManager.GrainInterfaceMapsBySilo;

            // Build the new map. Always start by himself
            var newSilosClusterGrainInterfaceMap = new Dictionary<SiloAddress, GrainInterfaceMap>
            {
                {this.Silo, grainTypeManager.GetTypeCodeMap()}
            };
            var getGrainInterfaceMapTasks = new List<Task<KeyValuePair<SiloAddress, GrainInterfaceMap>>>();


            foreach (var siloAddress in activeSilos.Keys)
            {
                if (siloAddress.Equals(this.Silo)) continue;

                GrainInterfaceMap value;
                if (knownSilosClusterGrainInterfaceMap.TryGetValue(siloAddress, out value))
                {
                    logger.Verbose3($"OnRefreshClusterMapTimer: value already found locally for {siloAddress}");
                    newSilosClusterGrainInterfaceMap[siloAddress] = value;
                }
                else
                {
                    // Value not found, let's get it
                    logger.Verbose3($"OnRefreshClusterMapTimer: value not found locally for {siloAddress}");
                    getGrainInterfaceMapTasks.Add(GetTargetSiloGrainInterfaceMap(siloAddress));
                }
            }

            if (getGrainInterfaceMapTasks.Any())
            {
                foreach (var keyValuePair in await Task.WhenAll(getGrainInterfaceMapTasks))
                {
                    if (keyValuePair.Value != null)
                        newSilosClusterGrainInterfaceMap.Add(keyValuePair.Key, keyValuePair.Value);
                }
            }

            grainTypeManager.SetInterfaceMapsBySilo(newSilosClusterGrainInterfaceMap);
        }

        private async Task<KeyValuePair<SiloAddress, GrainInterfaceMap>> GetTargetSiloGrainInterfaceMap(SiloAddress siloAddress)
        {
            try
            {
                var remoteTypeManager = InsideRuntimeClient.Current.InternalGrainFactory.GetSystemTarget<ISiloTypeManager>(Constants.TypeManagerId, siloAddress);
                var siloTypeCodeMap = await scheduler.QueueTask(() => remoteTypeManager.GetSiloTypeCodeMap(), SchedulingContext);
                return new KeyValuePair<SiloAddress, GrainInterfaceMap>(siloAddress, siloTypeCodeMap);
            }
            catch (Exception ex)
            {
				// Will be retried on the next timer hit
                logger.Error(ErrorCode.TypeManager_GetSiloGrainInterfaceMapError, $"Exception when trying to get GrainInterfaceMap for silos {siloAddress}", ex);
                
                return new KeyValuePair<SiloAddress, GrainInterfaceMap>(siloAddress, null);
            }
        }
    }
}


