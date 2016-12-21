using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime.MembershipService
{
    internal class MembershipTableFactory
    {
        private readonly IServiceProvider serviceProvider;
        private readonly AsyncLock initializationLock = new AsyncLock();
        private readonly Logger logger;
        private IMembershipTable membershipTable;

        public MembershipTableFactory(IServiceProvider serviceProvider)
        {
            this.serviceProvider = serviceProvider;
            logger = LogManager.GetLogger(nameof(MembershipTableFactory), LoggerType.Runtime);
        }

        internal async Task<IMembershipTable> GetMembershipTable()
        {
            if (membershipTable != null) return membershipTable;
            using (await this.initializationLock.LockAsync())
            {
                if (membershipTable != null) return membershipTable;

                var globalConfig = this.serviceProvider.GetRequiredService<GlobalConfiguration>();
                var livenessType = globalConfig.LivenessType;
                IMembershipTable result;
                if (livenessType.Equals(GlobalConfiguration.LivenessProviderType.MembershipTableGrain))
                {
                    var siloDetails = this.serviceProvider.GetRequiredService<SiloInitializationParameters>();
                    if (siloDetails.Type == Silo.SiloType.Primary)
                    {
                        logger.Info(ErrorCode.MembershipFactory1, "Creating membership table grain");
                        var catalog = this.serviceProvider.GetRequiredService<Catalog>();
                        await catalog.CreateSystemGrain(
                            Constants.SystemMembershipTableId,
                            typeof(GrainBasedMembershipTable).FullName);
                    }

                    var grainFactory = this.serviceProvider.GetRequiredService<IInternalGrainFactory>();
                    result =
                        grainFactory.Cast<IMembershipTableGrain>(GrainReference.FromGrainId(Constants.SystemMembershipTableId));
                }
                else if (livenessType.Equals(GlobalConfiguration.LivenessProviderType.SqlServer))
                {
                    result = AssemblyLoader.LoadAndCreateInstance<IMembershipTable>(Constants.ORLEANS_SQL_UTILS_DLL, this.logger);
                }
                else if (livenessType.Equals(GlobalConfiguration.LivenessProviderType.AzureTable))
                {
                    result = AssemblyLoader.LoadAndCreateInstance<IMembershipTable>(Constants.ORLEANS_AZURE_UTILS_DLL, this.logger);
                }
                else if (livenessType.Equals(GlobalConfiguration.LivenessProviderType.ZooKeeper))
                {
                    result = AssemblyLoader.LoadAndCreateInstance<IMembershipTable>(
                        Constants.ORLEANS_ZOOKEEPER_UTILS_DLL,
                        this.logger);
                }
                else if (livenessType.Equals(GlobalConfiguration.LivenessProviderType.Custom))
                {
                    result = AssemblyLoader.LoadAndCreateInstance<IMembershipTable>(
                        globalConfig.MembershipTableAssembly,
                        this.logger);
                }
                else
                {
                    throw new NotImplementedException("No membership table provider found for LivenessType=" + livenessType);
                }

                await WaitForTableToInit(result);
                membershipTable = result;
            }

            return membershipTable;
        }

        // Only used with MembershipTableGrain to wait for primary to start.
        private async Task WaitForTableToInit(IMembershipTable table)
        {
            var timespan = Debugger.IsAttached ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(5);
            // This is a quick temporary solution to enable primary node to start fully before secondaries.
            // Secondary silos waits untill GrainBasedMembershipTable is created. 
            for (int i = 0; i < 100; i++)
            {
                try
                {
                    await table.ReadAll().WithTimeout(timespan);
                    logger.Info(ErrorCode.MembershipTableGrainInit2, "-Connected to membership table provider.");
                    return;
                }
                catch (Exception exc)
                {
                    var type = exc.GetBaseException().GetType();
                    if (type == typeof(TimeoutException) || type == typeof(OrleansException))
                    {
                        logger.Info(
                            ErrorCode.MembershipTableGrainInit3,
                            "-Waiting for membership table provider to initialize. Going to sleep for {0} and re-try to reconnect.",
                            timespan);
                    }
                    else
                    {
                        logger.Info(ErrorCode.MembershipTableGrainInit4, "-Membership table provider failed to initialize. Giving up.");
                        throw;
                    }
                }

                await Task.Delay(timespan);
            }
        }
    }
}