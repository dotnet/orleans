using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.Configuration;

using LivenessProviderType = Orleans.Runtime.Configuration.GlobalConfiguration.LivenessProviderType;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Orleans.Runtime.MembershipService
{
    /// <summary>
    /// LegacyMembershipConfigurator configure membership table in the legacy way, which is from global configuration
    /// </summary>
    public interface ILegacyMembershipConfigurator
    {
        /// <summary>
        /// Configure the membership table in the legacy way 
        /// </summary>
        /// <returns></returns>
        IMembershipTable Configure();
    }

    internal class MembershipTableFactory
    {
        private readonly IServiceProvider serviceProvider;
        private readonly AsyncLock initializationLock = new AsyncLock();
        private readonly Logger logger;
        private IMembershipTable membershipTable;

        public MembershipTableFactory(IServiceProvider serviceProvider, LoggerWrapper<MembershipTableFactory> logger)
        {
            this.serviceProvider = serviceProvider;
            this.logger = logger;
        }

        private async Task<IMembershipTable> GetMembershipTableLegacy(GlobalConfiguration globalConfiguration)
        {
            ILegacyMembershipConfigurator configurator = null;
            switch (globalConfiguration.LivenessType)
            {
                case LivenessProviderType.MembershipTableGrain:
                    return await this.GetMembershipTableGrain();
                case LivenessProviderType.SqlServer:
                    configurator = AssemblyLoader.LoadAndCreateInstance<ILegacyMembershipConfigurator>(Constants.ORLEANS_SQL_UTILS_DLL, this.logger, this.serviceProvider);
                    break;
                case LivenessProviderType.AzureTable:
                    configurator = AssemblyLoader.LoadAndCreateInstance<ILegacyMembershipConfigurator>(Constants.ORLEANS_AZURE_UTILS_DLL, this.logger, this.serviceProvider);
                    break;
                case LivenessProviderType.ZooKeeper:
                    configurator = AssemblyLoader.LoadAndCreateInstance<ILegacyMembershipConfigurator>(
                        Constants.ORLEANS_ZOOKEEPER_UTILS_DLL,
                        this.logger,
                        this.serviceProvider);
                    break;
                case LivenessProviderType.Custom:
                    configurator = AssemblyLoader.LoadAndCreateInstance<ILegacyMembershipConfigurator>(
                        globalConfiguration.MembershipTableAssembly,
                        this.logger,
                        this.serviceProvider);
                    break;
                default:
                    break;
            }

            return configurator.Configure();
        }

        internal async Task<IMembershipTable> GetMembershipTable()
        {
            if (membershipTable != null) return membershipTable;
            using (await this.initializationLock.LockAsync())
            {
                if (membershipTable != null) return membershipTable;
                
                // get membership through DI
                var result = this.serviceProvider.GetService<IMembershipTable>();
                //if empty, then try to check if user configured using GranBasedMembership
                if (result == null)
                {
                    //if configured through UseGrainBasedMembershipTable method on ISiloHostBuilder, then this won't be null
                    var flag = this.serviceProvider.GetService<UseGrainBasedMembershipFlag>();
                    if(flag != null)
                        result = await this.GetMembershipTableGrain();
                }
                //if nothing found still, try load membership in the legacy way
                if (result == null)
                {
                    var globalConfig = this.serviceProvider.GetRequiredService<GlobalConfiguration>();
                    result = await GetMembershipTableLegacy(globalConfig);
                }
                //if still null, throw exception
                if (result == null)
                {
                    throw new NotImplementedException($"No membership table provider configured with Silo");
                }
                await result.InitializeMembershipTable(true);
                membershipTable = result;
            }

            return membershipTable;
        }
        
        private async Task<IMembershipTable> GetMembershipTableGrain()
        {
            var siloDetails = this.serviceProvider.GetRequiredService<SiloInitializationParameters>();
            var isPrimarySilo = siloDetails.Type == Silo.SiloType.Primary;
            if (isPrimarySilo)
            {
                this.logger.Info(ErrorCode.MembershipFactory1, "Creating membership table grain");
                var catalog = this.serviceProvider.GetRequiredService<Catalog>();
                await catalog.CreateSystemGrain(
                    Constants.SystemMembershipTableId,
                    typeof(GrainBasedMembershipTable).FullName);
            }

            var grainFactory = this.serviceProvider.GetRequiredService<IInternalGrainFactory>();
            var result = grainFactory.GetGrain<IMembershipTableGrain>(Constants.SystemMembershipTableId);

            if (isPrimarySilo)
            {
                await this.WaitForTableGrainToInit(result);
            }

            return result;
        }

        // Only used with MembershipTableGrain to wait for primary to start.
        private async Task WaitForTableGrainToInit(IMembershipTableGrain membershipTableGrain)
        {
            var timespan = Debugger.IsAttached ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(5);
            // This is a quick temporary solution to enable primary node to start fully before secondaries.
            // Secondary silos waits untill GrainBasedMembershipTable is created. 
            for (int i = 0; i < 100; i++)
            {
                try
                {
                    await membershipTableGrain.ReadAll().WithTimeout(timespan);
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