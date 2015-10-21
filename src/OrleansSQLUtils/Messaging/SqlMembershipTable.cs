/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using Orleans.Messaging;
using Orleans.Runtime.Configuration;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.SqlUtils;
using Orleans.SqlUtils.Management;


namespace Orleans.Runtime.MembershipService
{
    internal class SqlMembershipTable: IMembershipTable, IGatewayListProvider
    {
        private string deploymentId;        
        private TimeSpan maxStaleness;
        private TraceLogger logger;
        private IRelationalStorage database;
        private QueryConstantsBag queryConstants;

        public async Task InitializeMembershipTable(GlobalConfiguration config, bool tryInitTableVersion, TraceLogger traceLogger)
        {
            logger = traceLogger;
            deploymentId = config.DeploymentId;

            if (logger.IsVerbose3) logger.Verbose3("SqlMembershipTable.InitializeMembershipTable called.");

            database = RelationalStorageUtilities.CreateGenericStorageInstance(config.AdoInvariant, config.DataConnectionString);

            //This initializes all of Orleans operational queries from the database using a well known view
            //and assumes the database with appropriate defintions exists already.
            queryConstants = await database.InitializeOrleansQueriesAsync();

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


        public async Task InitializeGatewayListProvider(ClientConfiguration config, TraceLogger traceLogger)
        {
            logger = traceLogger;
            if (logger.IsVerbose3) logger.Verbose3("SqlMembershipTable.InitializeGatewayListProvider called.");

            deploymentId = config.DeploymentId;            
            maxStaleness = config.GatewayListRefreshPeriod;
            database = RelationalStorageUtilities.CreateGenericStorageInstance(config.AdoInvariant, config.DataConnectionString);

            //This initializes all of Orleans operational queries from the database using a well known view
            //and assumes the database with appropriate defintions exists already.
            queryConstants = await database.InitializeOrleansQueriesAsync();
        }


        public TimeSpan MaxStaleness
        {
            get { return maxStaleness; }
        }


        public bool IsUpdatable
        {
            get { return true; }
        }


        public Task<IList<Uri>> GetGateways()
        {
            if (logger.IsVerbose3) logger.Verbose3("SqlMembershipTable.GetGateways called.");
            try
            {                
                var query = queryConstants.GetConstant(database.InvariantName, QueryKeys.ActiveGatewaysQuery);
                return database.ActiveGatewaysAsync(query, deploymentId);
            }
            catch(Exception ex)
            {
                if (logger.IsVerbose) logger.Verbose("SqlMembershipTable.Gateways failed {0}", ex);
                throw;
            }
        }


        Task<MembershipTableData> IMembershipTable.ReadRow(SiloAddress key)
        {
            if (logger.IsVerbose3) logger.Verbose3(string.Format("SqlMembershipTable.ReadRow called with key: {0}.", key));
            try
            {
                var query = queryConstants.GetConstant(database.InvariantName, QueryKeys.MembershipReadRowKey);
                return database.MembershipDataAsync(query, deploymentId, key);                
            }
            catch(Exception ex)
            {
                if (logger.IsVerbose) logger.Verbose("SqlMembershipTable.ReadRow failed: {0}", ex);
                throw;
            }
        }


        Task<MembershipTableData> IMembershipTable.ReadAll()
        {
            if (logger.IsVerbose3) logger.Verbose3("SqlMembershipTable.ReadAll called.");
            try
            {
                var query = queryConstants.GetConstant(database.InvariantName, QueryKeys.MembershipReadAllKey);
                return database.AllMembershipDataAsync(query, deploymentId);                
            }
            catch(Exception ex)
            {
                if (logger.IsVerbose) logger.Verbose("SqlMembershipTable.ReadAll failed: {0}", ex);
                throw;
            }
        }


        Task<bool> IMembershipTable.InsertRow(MembershipEntry entry, TableVersion tableVersion)
        {
            if (logger.IsVerbose3) logger.Verbose3(string.Format("SqlMembershipTable.InsertRow called with entry {0} and tableVersion {1}.", entry, tableVersion));

            //The "tableVersion" parameter should always exist when inserting a row as Init should
            //have been called and membership version created and read. This is an optimization to
            //not to go through all the way to database to fail a conditional check on etag (which does
            //exist for the sake of robustness) as mandated by Orleans membership protocol.
            //Likewise, no update can be done without membership entry.
            if (entry == null)
            {
                if (logger.IsVerbose) logger.Verbose("SqlMembershipTable.InsertRow aborted due to null check. MembershipEntry is null.");
                throw new ArgumentNullException("entry");
            }
            if (tableVersion == null)
            {
                if (logger.IsVerbose) logger.Verbose("SqlMembershipTable.InsertRow aborted due to null check. TableVersion is null ");
                throw new ArgumentNullException("tableVersion");
            }

            try
            {
                var query = queryConstants.GetConstant(database.InvariantName, QueryKeys.InsertMembershipKey);
                return database.InsertMembershipRowAsync(query, deploymentId, entry, tableVersion);
            }
            catch(Exception ex)
            {
                if (logger.IsVerbose) logger.Verbose("SqlMembershipTable.InsertRow failed: {0}", ex);
                throw;
            }            
        }


        Task<bool> IMembershipTable.UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
        {
            if (logger.IsVerbose3) logger.Verbose3(string.Format("IMembershipTable.UpdateRow called with entry {0}, etag {1} and tableVersion {2}.", entry, etag, tableVersion));

            //The "tableVersion" parameter should always exist when updating a row as Init should
            //have been called and membership version created and read. This is an optimization to
            //not to go through all the way to database to fail a conditional check (which does
            //exist for the sake of robustness) as mandated by Orleans membership protocol.
            //Likewise, no update can be done without membership entry or an etag.
            if (entry == null)
            {
                if (logger.IsVerbose) logger.Verbose("SqlMembershipTable.UpdateRow aborted due to null check. MembershipEntry is null.");
                throw new ArgumentNullException("entry");
            }
            if (etag == null)
            {
                if (logger.IsVerbose) logger.Verbose("SqlMembershipTable.UpdateRow aborted due to null check. etag is null.");
                throw new ArgumentNullException("etag");
            }
            if (tableVersion == null)
            {
                if (logger.IsVerbose) logger.Verbose("SqlMembershipTable.UpdateRow aborted due to null check. TableVersion is null ");
                throw new ArgumentNullException("tableVersion");
            }

            try
            {
                var query = queryConstants.GetConstant(database.InvariantName, QueryKeys.UpdateMembershipKey);
                return database.UpdateMembershipRowAsync(query, deploymentId, etag, entry, tableVersion);                                
            }
            catch(Exception ex)
            {
                if (logger.IsVerbose) logger.Verbose("SqlMembershipTable.UpdateRow failed: {0}", ex);
                throw;
            }
        }


        Task IMembershipTable.UpdateIAmAlive(MembershipEntry entry)
        {
            if(logger.IsVerbose3) logger.Verbose3(string.Format("IMembershipTable.UpdateIAmAlive called with entry {0}.", entry));
            if (entry == null)
            {
                if (logger.IsVerbose) logger.Verbose("SqlMembershipTable.UpdateIAmAlive aborted due to null check. MembershipEntry is null.");
                throw new ArgumentNullException("entry");
            }
            try
            {
                var query = queryConstants.GetConstant(database.InvariantName, QueryKeys.UpdateIAmAlivetimeKey);
                return database.UpdateIAmAliveTimeAsync(query, deploymentId, entry);
            }
            catch(Exception ex)
            {
                if (logger.IsVerbose) logger.Verbose("SqlMembershipTable.UpdateIAmAlive failed: {0}", ex);
                throw;
            }
        }


        Task IMembershipTable.DeleteMembershipTableEntries(string deploymentId)
        {
            if (logger.IsVerbose3) logger.Verbose3(string.Format("IMembershipTable.DeleteMembershipTableEntries called with deploymentId {0}.", deploymentId));
            try
            {
                var query = queryConstants.GetConstant(database.InvariantName, QueryKeys.DeleteMembershipTableEntriesKey);
                return database.DeleteMembershipTableEntriesAsync(query, deploymentId);
            }
            catch(Exception ex)
            {
                if (logger.IsVerbose) logger.Verbose("SqlMembershipTable.DeleteMembershipTableEntries failed: {0}", ex);
                throw;
            }
        }
               
        
        private Task<bool> InitTableAsync()
        {
            try
            {
                var query = queryConstants.GetConstant(database.InvariantName, QueryKeys.InsertMembershipVersionKey);
                return database.InsertMembershipVersionRowAsync(query, deploymentId, 0);
            }
            catch(Exception ex)
            {
                if(logger.IsVerbose2) logger.Verbose2("Insert silo membership version failed: {0}", ex.ToString());
            }
            return Task.FromResult(false);
        }
    }
}
