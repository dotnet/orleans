using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Concurrency;
using Orleans.Configuration;
using Orleans.Internal;
using Orleans.Runtime.Providers;
using Orleans.Serialization;

namespace Orleans.Runtime.MembershipService
{
    internal class SystemTargetBasedMembershipTable : IMembershipTable
    {
        private readonly IServiceProvider serviceProvider;
        private readonly ILogger logger;
        private IMembershipTableSystemTarget grain;

        public SystemTargetBasedMembershipTable(IServiceProvider serviceProvider, ILogger<SystemTargetBasedMembershipTable> logger)
        {
            this.serviceProvider = serviceProvider;
            this.logger = logger;
        }
        public async Task InitializeMembershipTable(bool tryInitTableVersion)
        {
            this.grain = await GetMembershipTable();
        }

        private async Task<IMembershipTableSystemTarget> GetMembershipTable()
        {
            var options = this.serviceProvider.GetRequiredService<IOptions<DevelopmentClusterMembershipOptions>>().Value;
            if (options.PrimarySiloEndpoint == null)
            {
                throw new OrleansConfigurationException(
                    $"{nameof(DevelopmentClusterMembershipOptions)}.{nameof(options.PrimarySiloEndpoint)} must be set when using development clustering.");
            }

            var siloDetails = this.serviceProvider.GetService<ILocalSiloDetails>();
            bool isPrimarySilo = siloDetails.SiloAddress.Endpoint.Equals(options.PrimarySiloEndpoint);
            if (isPrimarySilo)
            {
                this.logger.Info(ErrorCode.MembershipFactory1, "Creating in-memory membership table");
                var catalog = serviceProvider.GetRequiredService<Catalog>();
                catalog.RegisterSystemTarget(ActivatorUtilities.CreateInstance<MembershipTableSystemTarget>(serviceProvider));
            }

            var grainFactory = this.serviceProvider.GetRequiredService<IInternalGrainFactory>();
            var result = grainFactory.GetSystemTarget<IMembershipTableSystemTarget>(Constants.SystemMembershipTableType, SiloAddress.New(options.PrimarySiloEndpoint, 0));
            if (isPrimarySilo)
            {
                await this.WaitForTableGrainToInit(result);
            }

            return result;
        }

        // Only used with MembershipTableGrain to wait for primary to start.
        private async Task WaitForTableGrainToInit(IMembershipTableSystemTarget membershipTableSystemTarget)
        {
            var timespan = Debugger.IsAttached ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(5);
            // This is a quick temporary solution to enable primary node to start fully before secondaries.
            // Secondary silos waits untill GrainBasedMembershipTable is created. 
            for (int i = 0; i < 100; i++)
            {
                try
                {
                    await membershipTableSystemTarget.ReadAll().WithTimeout(timespan, $"MembershipGrain trying to read all content of the membership table, failed due to timeout {timespan}");
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

        public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate)
        {
            throw new NotImplementedException();
        }
    }

    [Reentrant]
    internal class MembershipTableSystemTarget : SystemTarget, IMembershipTableSystemTarget
    {
        private InMemoryMembershipTable table;
        private readonly ILogger logger;

        public MembershipTableSystemTarget(
            ILocalSiloDetails localSiloDetails,
            ILoggerFactory loggerFactory,
            SerializationManager serializationManager)
            : base(CreateId(localSiloDetails), localSiloDetails.SiloAddress, lowPriority: false, loggerFactory)
        {
            logger = loggerFactory.CreateLogger<MembershipTableSystemTarget>();
            table = new InMemoryMembershipTable(serializationManager);
            logger.Info(ErrorCode.MembershipGrainBasedTable1, "GrainBasedMembershipTable Activated.");
        }

        private static SystemTargetGrainId CreateId(ILocalSiloDetails localSiloDetails)
        {
            return SystemTargetGrainId.Create(Constants.SystemMembershipTableType, SiloAddress.New(localSiloDetails.SiloAddress.Endpoint, 0));
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

        public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate)
        {
            throw new NotImplementedException();
        }
    }
}