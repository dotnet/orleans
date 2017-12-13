using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Clustering.AdoNet.Storage;
using OrleansSQLUtils.Configuration;

namespace Orleans.Runtime.MembershipService
{ 
    public class SqlMembershipTable: IMembershipTable
    {
        private readonly IGrainReferenceConverter grainReferenceConverter;
        private string clusterId;        
        private ILogger logger;
        private RelationalOrleansQueries orleansQueries;
        private readonly SqlMembershipOptions membershipTableOptions;
        public SqlMembershipTable(IGrainReferenceConverter grainReferenceConverter, IOptions<SiloOptions> siloOptions, IOptions<SqlMembershipOptions> membershipTableoptions, ILogger<SqlMembershipTable> logger)
        {
            this.grainReferenceConverter = grainReferenceConverter;
            this.logger = logger;
            this.membershipTableOptions = membershipTableoptions.Value;
            this.clusterId = siloOptions.Value.ClusterId;
        }

        public async Task InitializeMembershipTable(bool tryInitTableVersion)
        {
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("SqlMembershipTable.InitializeMembershipTable called.");

            //This initializes all of Orleans operational queries from the database using a well known view
            //and assumes the database with appropriate definitions exists already.
            orleansQueries = await RelationalOrleansQueries.CreateInstance(membershipTableOptions.AdoInvariant, membershipTableOptions.ConnectionString, this.grainReferenceConverter);
            
            // even if I am not the one who created the table, 
            // try to insert an initial table version if it is not already there,
            // so we always have a first table version row, before this silo starts working.
            if(tryInitTableVersion)
            {
                var wasCreated = await InitTableAsync();
                if(wasCreated)
                {
                    logger.Info("Created new table version row.");
                }
            }
        }


        public async Task<MembershipTableData> ReadRow(SiloAddress key)
        {
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace(string.Format("SqlMembershipTable.ReadRow called with key: {0}.", key));
            try
            {
                return await orleansQueries.MembershipReadRowAsync(this.clusterId, key);                
            }
            catch(Exception ex)
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("SqlMembershipTable.ReadRow failed: {0}", ex);
                throw;
            }
        }


        public async Task<MembershipTableData> ReadAll()
        {
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("SqlMembershipTable.ReadAll called.");
            try
            {
                return await orleansQueries.MembershipReadAllAsync(this.clusterId);                
            }
            catch(Exception ex)
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("SqlMembershipTable.ReadAll failed: {0}", ex);
                throw;
            }
        }


        public async Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion)
        {
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace(string.Format("SqlMembershipTable.InsertRow called with entry {0} and tableVersion {1}.", entry, tableVersion));

            //The "tableVersion" parameter should always exist when inserting a row as Init should
            //have been called and membership version created and read. This is an optimization to
            //not to go through all the way to database to fail a conditional check on etag (which does
            //exist for the sake of robustness) as mandated by Orleans membership protocol.
            //Likewise, no update can be done without membership entry.
            if (entry == null)
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("SqlMembershipTable.InsertRow aborted due to null check. MembershipEntry is null.");
                throw new ArgumentNullException("entry");
            }
            if (tableVersion == null)
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("SqlMembershipTable.InsertRow aborted due to null check. TableVersion is null ");
                throw new ArgumentNullException("tableVersion");
            }

            try
            {
                return await orleansQueries.InsertMembershipRowAsync(this.clusterId, entry, tableVersion.VersionEtag);
            }
            catch(Exception ex)
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("SqlMembershipTable.InsertRow failed: {0}", ex);
                throw;
            }            
        }


        public async Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
        {
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace(string.Format("IMembershipTable.UpdateRow called with entry {0}, etag {1} and tableVersion {2}.", entry, etag, tableVersion));

            //The "tableVersion" parameter should always exist when updating a row as Init should
            //have been called and membership version created and read. This is an optimization to
            //not to go through all the way to database to fail a conditional check (which does
            //exist for the sake of robustness) as mandated by Orleans membership protocol.
            //Likewise, no update can be done without membership entry or an etag.
            if (entry == null)
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("SqlMembershipTable.UpdateRow aborted due to null check. MembershipEntry is null.");
                throw new ArgumentNullException("entry");
            }
            if (tableVersion == null)
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("SqlMembershipTable.UpdateRow aborted due to null check. TableVersion is null ");
                throw new ArgumentNullException("tableVersion");
            }

            try
            {
                return await orleansQueries.UpdateMembershipRowAsync(this.clusterId, entry, tableVersion.VersionEtag);                                
            }
            catch(Exception ex)
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("SqlMembershipTable.UpdateRow failed: {0}", ex);
                throw;
            }
        }


        public async Task UpdateIAmAlive(MembershipEntry entry)
        {
            if(logger.IsEnabled(LogLevel.Trace)) logger.Trace(string.Format("IMembershipTable.UpdateIAmAlive called with entry {0}.", entry));
            if (entry == null)
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("SqlMembershipTable.UpdateIAmAlive aborted due to null check. MembershipEntry is null.");
                throw new ArgumentNullException("entry");
            }
            try
            {
                await orleansQueries.UpdateIAmAliveTimeAsync(this.clusterId, entry.SiloAddress, entry.IAmAliveTime);
            }
            catch(Exception ex)
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("SqlMembershipTable.UpdateIAmAlive failed: {0}", ex);
                throw;
            }
        }


        public async Task DeleteMembershipTableEntries(string clusterId)
        {
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace(string.Format("IMembershipTable.DeleteMembershipTableEntries called with clusterId {0}.", clusterId));
            try
            {
                await orleansQueries.DeleteMembershipTableEntriesAsync(clusterId);
            }
            catch(Exception ex)
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("SqlMembershipTable.DeleteMembershipTableEntries failed: {0}", ex);
                throw;
            }
        }
               
        
        private async Task<bool> InitTableAsync()
        {
            try
            {
                return await orleansQueries.InsertMembershipVersionRowAsync(this.clusterId);
            }
            catch(Exception ex)
            {
                if(logger.IsEnabled(LogLevel.Trace)) logger.Trace("Insert silo membership version failed: {0}", ex.ToString());
                throw;
            }
        }
    }
}
