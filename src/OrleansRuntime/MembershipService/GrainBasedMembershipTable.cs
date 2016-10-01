using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.MultiCluster;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime.MembershipService
{
    [Reentrant]
    [OneInstancePerCluster]
    internal class GrainBasedMembershipTable : Grain, IMembershipTableGrain
    {
        private InMemoryMembershipTable table;
        private Logger logger;

        public override Task OnActivateAsync()
        {
            logger = LogManager.GetLogger("GrainBasedMembershipTable", LoggerType.Runtime);
            logger.Info(ErrorCode.MembershipGrainBasedTable1, "GrainBasedMembershipTable Activated.");
            table = new InMemoryMembershipTable();
            return TaskDone.Done;
        }

        public override Task OnDeactivateAsync()
        {
            logger.Info("GrainBasedMembershipTable Deactivated.");
            return TaskDone.Done;
        }

        public Task InitializeMembershipTable(GlobalConfiguration config, bool tryInitTableVersion, Logger traceLogger)
        {
            logger.Info("InitializeMembershipTable {0}.", tryInitTableVersion);
            return TaskDone.Done;
        }

        public Task DeleteMembershipTableEntries(string deploymentId)
        {
            logger.Info("DeleteMembershipTableEntries {0}", deploymentId);
            table = null;
            return TaskDone.Done;
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
            if (logger.IsVerbose) logger.Verbose("InsertRow entry = {0}, table version = {1}", entry.ToFullString(), tableVersion);
            bool result = table.Insert(entry, tableVersion);
            if (result == false)
                logger.Info(ErrorCode.MembershipGrainBasedTable2, 
                    "Insert of {0} and table version {1} failed. Table now is {2}",
                    entry.ToFullString(), tableVersion, table.ReadAll());

            return Task.FromResult(result);
        }

        public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
        {
            if (logger.IsVerbose) logger.Verbose("UpdateRow entry = {0}, etag = {1}, table version = {2}", entry.ToFullString(), etag, tableVersion);
            bool result = table.Update(entry, etag, tableVersion);
            if (result == false)
                logger.Info(ErrorCode.MembershipGrainBasedTable3,
                    "Update of {0}, eTag {1}, table version {2} failed. Table now is {3}",
                    entry.ToFullString(), etag, tableVersion, table.ReadAll());

            return Task.FromResult(result);
        }

        public Task UpdateIAmAlive(MembershipEntry entry)
        {
            if (logger.IsVerbose) logger.Verbose("UpdateIAmAlive entry = {0}", entry.ToFullString());
            table.UpdateIAmAlive(entry);
            return TaskDone.Done;
        }
    }
}

