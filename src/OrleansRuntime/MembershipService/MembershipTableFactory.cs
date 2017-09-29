using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.Configuration;

using LivenessProviderType = Orleans.Runtime.Configuration.GlobalConfiguration.LivenessProviderType;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.MembershipService
{
    internal class MembershipTableFactory
    {
        private readonly IServiceProvider serviceProvider;
        private readonly AsyncLock initializationLock = new AsyncLock();
        private readonly ILogger logger;
        private IMembershipTable membershipTable;

        public MembershipTableFactory(IServiceProvider serviceProvider, ILogger<MembershipTableFactory> logger)
        {
            this.serviceProvider = serviceProvider;
            this.logger = logger;
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
                    var options = this.serviceProvider.GetService<GrainBasedMembershipTableOptions>();
                    //if configured through legacy GlobalConfiguration, then livenessProviderType should set to MembershipTableGrain
                    var globalConfig = this.serviceProvider.GetService<GlobalConfiguration>();
                    if(options != null || globalConfig.LivenessType == LivenessProviderType.MembershipTableGrain)
                        result = await this.GetMembershipTableGrain();
                }
                //if nothing found still, throw exception
                if(result == null)
                    throw new NotImplementedException(
                        $"No membership table provider configured with Silo");

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