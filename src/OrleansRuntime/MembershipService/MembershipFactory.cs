using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime.MembershipService
{
    internal class MembershipFactory
    {
        private readonly IInternalGrainFactory grainFactory;

        private readonly Logger logger;

        public MembershipFactory(IInternalGrainFactory grainFactory)
        {
            this.grainFactory = grainFactory;
            logger = LogManager.GetLogger("MembershipFactory", LoggerType.Runtime);
        }

        internal Task CreateMembershipTableProvider(Catalog catalog, Silo silo)
        {
            var livenessType = silo.GlobalConfig.LivenessType;
            logger.Info(ErrorCode.MembershipFactory1, "Creating membership table provider for type={0}", Enum.GetName(typeof(GlobalConfiguration.LivenessProviderType), livenessType));
            if (livenessType.Equals(GlobalConfiguration.LivenessProviderType.MembershipTableGrain))
            {
                return catalog.CreateSystemGrain(
                        Constants.SystemMembershipTableId,
                        typeof(GrainBasedMembershipTable).FullName);
            }
            return TaskDone.Done;
        }

        internal MembershipOracle CreateMembershipOracle(Silo silo, IMembershipTable membershipTable)
        {
            var livenessType = silo.GlobalConfig.LivenessType;
            logger.Info("Creating membership oracle for type={0}", Enum.GetName(typeof(GlobalConfiguration.LivenessProviderType), livenessType));
            return new MembershipOracle(silo, membershipTable);
        }

        internal IMembershipTable GetMembershipTable(GlobalConfiguration globalConfig)
        {
            var livenessType = globalConfig.LivenessType;
            IMembershipTable membershipTable;
            if (livenessType.Equals(GlobalConfiguration.LivenessProviderType.MembershipTableGrain))
            {
                membershipTable =
                    this.grainFactory.Cast<IMembershipTableGrain>(GrainReference.FromGrainId(Constants.SystemMembershipTableId));
            }
            else if (livenessType.Equals(GlobalConfiguration.LivenessProviderType.SqlServer))
            {
                membershipTable = AssemblyLoader.LoadAndCreateInstance<IMembershipTable>(Constants.ORLEANS_SQL_UTILS_DLL, this.logger);
            }
            else if (livenessType.Equals(GlobalConfiguration.LivenessProviderType.AzureTable))
            {
                membershipTable = AssemblyLoader.LoadAndCreateInstance<IMembershipTable>(Constants.ORLEANS_AZURE_UTILS_DLL, this.logger);
            }
            else if (livenessType.Equals(GlobalConfiguration.LivenessProviderType.ZooKeeper))
            {
                membershipTable = AssemblyLoader.LoadAndCreateInstance<IMembershipTable>(Constants.ORLEANS_ZOOKEEPER_UTILS_DLL, this.logger);
            }
            else if (livenessType.Equals(GlobalConfiguration.LivenessProviderType.Custom))
            {
                membershipTable = AssemblyLoader.LoadAndCreateInstance<IMembershipTable>(globalConfig.MembershipTableAssembly, this.logger);
            }
            else
            {
                throw new NotImplementedException("No membership table provider found for LivenessType=" + livenessType);
            }

            return membershipTable;
        }

        // Only used with MembershipTableGrain to wait for primary to start.
        internal async Task WaitForTableToInit(IMembershipTable membershipTable)
        {
            var timespan = Debugger.IsAttached ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(5);
            // This is a quick temporary solution to enable primary node to start fully before secondaries.
            // Secondary silos waits untill GrainBasedMembershipTable is created. 
            for (int i = 0; i < 100; i++)
            {
                bool needToWait = false;
                try
                {
                    MembershipTableData table = await membershipTable.ReadAll().WithTimeout(timespan);
                    logger.Info(ErrorCode.MembershipTableGrainInit2, "-Connected to membership table provider.");
                    return;
                }
                catch (Exception exc)
                {
                    var type = exc.GetBaseException().GetType();
                    if (type == typeof(TimeoutException) || type == typeof(OrleansException))
                    {
                        logger.Info(ErrorCode.MembershipTableGrainInit3,
                            "-Waiting for membership table provider to initialize. Going to sleep for {0} and re-try to reconnect.", timespan);
                        needToWait = true;
                    }
                    else
                    {
                        logger.Info(ErrorCode.MembershipTableGrainInit4, "-Membership table provider failed to initialize. Giving up.");
                        throw;
                    }
                }

                if (needToWait)
                {
                    await Task.Delay(timespan);
                }
            }
        }
    }
}
