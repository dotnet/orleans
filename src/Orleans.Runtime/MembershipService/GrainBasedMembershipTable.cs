using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Concurrency;
using Orleans.Hosting;
using Orleans.MultiCluster;
using Orleans.Serialization;

namespace Orleans.Runtime.MembershipService
{
    internal class GrainBasedMembershipTable : IMembershipTable
    {
        private readonly IServiceProvider serviceProvider;
        private readonly ILogger logger;
        private IMembershipTableGrain grain;

        public GrainBasedMembershipTable(IServiceProvider serviceProvider, ILogger<MembershipTableFactory> logger)
        {
            this.serviceProvider = serviceProvider;
            this.logger = logger;
        }
        public async Task InitializeMembershipTable(bool tryInitTableVersion)
        {
            this.grain = await GetMembershipTableGrain();
        }

        private async Task<IMembershipTableGrain> GetMembershipTableGrain()
        {
            var options = this.serviceProvider.GetRequiredService<IOptions<DevelopmentMembershipOptions>>().Value;
            var siloDetails = this.serviceProvider.GetService<ILocalSiloDetails>();
            bool isPrimarySilo = siloDetails.SiloAddress.Endpoint.Equals(options.PrimarySiloEndpoint);
            if (isPrimarySilo)
            {
                this.logger.Info(ErrorCode.MembershipFactory1, "Creating membership table grain");
                var catalog = this.serviceProvider.GetRequiredService<Catalog>();
                await catalog.CreateSystemGrain(
                    Constants.SystemMembershipTableId,
                    typeof(GrainBasedMembershipTableGrain).FullName);
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

        public Task DeleteMembershipTableEntries(string clusterId) => this.grain.DeleteMembershipTableEntries(clusterId);

        public Task<MembershipTableData> ReadRow(SiloAddress key) => this.grain.ReadRow(key);

        public Task<MembershipTableData> ReadAll() => this.grain.ReadAll();

        public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion) => this.grain.InsertRow(entry, tableVersion);

        public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion) => this.grain.UpdateRow(entry, etag, tableVersion);

        public Task UpdateIAmAlive(MembershipEntry entry) => this.grain.UpdateIAmAlive(entry);
    }

    [Reentrant]
    [OneInstancePerCluster]
    internal class GrainBasedMembershipTableGrain : Grain, IMembershipTableGrain
    {
        private InMemoryMembershipTable table;
        private ILogger logger;

        public override Task OnActivateAsync()
        {
            logger = this.ServiceProvider.GetRequiredService<ILogger<GrainBasedMembershipTableGrain>>();
            logger.Info(ErrorCode.MembershipGrainBasedTable1, "GrainBasedMembershipTable Activated.");
            table = new InMemoryMembershipTable(this.ServiceProvider.GetRequiredService<SerializationManager>());
            return Task.CompletedTask;
        }

        public override Task OnDeactivateAsync()
        {
            logger.Info("GrainBasedMembershipTable Deactivated.");
            return Task.CompletedTask;
        }

        public Task InitializeMembershipTable(bool tryInitTableVersion)
        {
            logger.Info("InitializeMembershipTable {0}.", tryInitTableVersion);
            return Task.CompletedTask;
        }

        public Task DeleteMembershipTableEntries(string clusterId)
        {
            logger.Info("DeleteMembershipTableEntries {0}", clusterId);
            table = null;
            return Task.CompletedTask;
        }

        public Task<MembershipTableData> ReadRow(SiloAddress key)
        {
            return Task.FromResult(table.Read(key));
        }

        public Task<MembershipTableData> ReadAll()
        {
            var t = table.ReadAll();
            return Task.FromResult(t);
        }

        public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion)
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("InsertRow entry = {0}, table version = {1}", entry.ToFullString(), tableVersion);
            bool result = table.Insert(entry, tableVersion);
            if (result == false)
                logger.Info(ErrorCode.MembershipGrainBasedTable2, 
                    "Insert of {0} and table version {1} failed. Table now is {2}",
                    entry.ToFullString(), tableVersion, table.ReadAll());

            return Task.FromResult(result);
        }

        public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("UpdateRow entry = {0}, etag = {1}, table version = {2}", entry.ToFullString(), etag, tableVersion);
            bool result = table.Update(entry, etag, tableVersion);
            if (result == false)
                logger.Info(ErrorCode.MembershipGrainBasedTable3,
                    "Update of {0}, eTag {1}, table version {2} failed. Table now is {3}",
                    entry.ToFullString(), etag, tableVersion, table.ReadAll());

            return Task.FromResult(result);
        }

        public Task UpdateIAmAlive(MembershipEntry entry)
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("UpdateIAmAlive entry = {0}", entry.ToFullString());
            table.UpdateIAmAlive(entry);
            return Task.CompletedTask;
        }
    }
}

