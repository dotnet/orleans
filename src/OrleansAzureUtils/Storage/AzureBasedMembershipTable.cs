using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage;
using Orleans.AzureUtils;
using Orleans.Runtime.Configuration;


namespace Orleans.Runtime.MembershipService
{
    internal class AzureBasedMembershipTable : IMembershipTable
    {
        private TraceLogger logger;
        private OrleansSiloInstanceManager tableManager;

        public async Task InitializeMembershipTable(GlobalConfiguration config, bool tryInitTableVersion, TraceLogger traceLogger)
        {
            logger = traceLogger;
            AzureTableDefaultPolicies.MaxBusyRetries = config.MaxStorageBusyRetries;
            TraceLogger.SetExceptionDecoder(typeof(StorageException), AzureStorageUtils.PrintStorageException);

            tableManager = await OrleansSiloInstanceManager.GetManager(
                config.DeploymentId, config.DataConnectionString);

            // even if I am not the one who created the table, 
            // try to insert an initial table version if it is not already there,
            // so we always have a first table version row, before this silo starts working.
            if (tryInitTableVersion)
            {
                // ignore return value, since we don't care if I inserted it or not, as long as it is in there. 
                bool created = await tableManager.TryCreateTableVersionEntryAsync();
                if(created) logger.Info("Created new table version row.");
            }
        }

        public Task DeleteMembershipTableEntries(string deploymentId)
        {
            return tableManager.DeleteTableEntries(deploymentId);
        }

        public async Task<MembershipTableData> ReadRow(SiloAddress key)
        {
            try
            {
                var entries = await tableManager.FindSiloEntryAndTableVersionRow(key);
                MembershipTableData data = Convert(entries);
                if (logger.IsVerbose2) logger.Verbose2("Read my entry {0} Table=" + Environment.NewLine + "{1}", key.ToLongString(), data.ToString());
                return data;
            }
            catch (Exception exc)
            {
                logger.Warn(ErrorCode.AzureTable_20, String.Format("Intermediate error reading silo entry for key {0} from the table {1}.",
                                key.ToLongString(), tableManager.TableName), exc);
                throw;
            }
        }

        public async Task<MembershipTableData> ReadAll()
        {
             try
             {
                var entries = await tableManager.FindAllSiloEntries();   
                MembershipTableData data = Convert(entries);
                if (logger.IsVerbose2) logger.Verbose2("ReadAll Table=" + Environment.NewLine + "{0}", data.ToString());

                return data; 
            }
            catch (Exception exc)
            {
                logger.Warn(ErrorCode.AzureTable_21, String.Format(
                    "Intermediate error reading all silo entries {0}.", tableManager.TableName), exc);
                throw;
            }
        }

        public async Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion)
        {
            try
            {
                if (logger.IsVerbose) logger.Verbose("InsertRow entry = {0}, table version = {1}", entry.ToFullString(), tableVersion);
                var tableEntry = Convert(entry, tableManager.DeploymentId);
                var versionEntry = tableManager.CreateTableVersionEntry(tableVersion.Version);

                bool result = await tableManager.InsertSiloEntryConditionally(
                    tableEntry, versionEntry, tableVersion.VersionEtag);

                if (result == false)
                    logger.Warn(ErrorCode.AzureTable_22, String.Format("Insert failed due to contention on the table. Will retry. Entry {0}, table version = {1}", entry.ToFullString(), tableVersion));
                return result;
            }
            catch (Exception exc)
            {
                logger.Warn(ErrorCode.AzureTable_23, String.Format("Intermediate error inserting entry {0} tableVersion {1} to the table {2}.",
                    entry.ToFullString(), (tableVersion == null ? "null" : tableVersion.ToString()), tableManager.TableName), exc);
                throw;
            }
        }

        public async Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
        {
            try
            {
                if (logger.IsVerbose) logger.Verbose("UpdateRow entry = {0}, etag = {1}, table version = {2}", entry.ToFullString(), etag, tableVersion);
                var siloEntry = Convert(entry, tableManager.DeploymentId);
                var versionEntry = tableManager.CreateTableVersionEntry(tableVersion.Version);

                bool result = await tableManager.UpdateSiloEntryConditionally(siloEntry, etag, versionEntry, tableVersion.VersionEtag);
                if (result == false)
                    logger.Warn(ErrorCode.AzureTable_24, String.Format("Update failed due to contention on the table. Will retry. Entry {0}, eTag {1}, table version = {2} ", entry.ToFullString(), etag, tableVersion));
                return result;
            }
            catch (Exception exc)
            {
                logger.Warn(ErrorCode.AzureTable_25, String.Format("Intermediate error updating entry {0} tableVersion {1} to the table {2}.",
                        entry.ToFullString(), (tableVersion == null ? "null" : tableVersion.ToString()), tableManager.TableName), exc);
                throw;
            }
        }

        public async Task UpdateIAmAlive(MembershipEntry entry)
        {
            try
            {
                if (logger.IsVerbose) logger.Verbose("Merge entry = {0}", entry.ToFullString());
                var siloEntry = ConvertPartial(entry, tableManager.DeploymentId);
                await tableManager.MergeTableEntryAsync(siloEntry);
            }
            catch (Exception exc)
            {
                logger.Warn(ErrorCode.AzureTable_26, String.Format("Intermediate error updating IAmAlive field for entry {0} to the table {1}.", entry.ToFullString(), tableManager.TableName), exc);
                throw;
            }
        }

        private MembershipTableData Convert(List<Tuple<SiloInstanceTableEntry, string>> entries)
        {
            try
            {
                var memEntries = new List<Tuple<MembershipEntry, string>>();
                TableVersion tableVersion = null;
                foreach (var tuple in entries)
                {
                    var tableEntry = tuple.Item1;
                    if (tableEntry.RowKey.Equals(SiloInstanceTableEntry.TABLE_VERSION_ROW))
                    {
                        tableVersion = new TableVersion(Int32.Parse(tableEntry.MembershipVersion), tuple.Item2);
                    }
                    else
                    {
                        try
                        {
                            
                            MembershipEntry membershipEntry = Parse(tableEntry);
                            memEntries.Add(new Tuple<MembershipEntry, string>(membershipEntry, tuple.Item2));
                        }
                        catch (Exception exc)
                        {
                            logger.Error(ErrorCode.AzureTable_61, String.Format(
                                "Intermediate error parsing SiloInstanceTableEntry to MembershipTableData: {0}. Ignoring this entry.",
                                tableEntry), exc);
                        }
                    }
                }
                var data = new MembershipTableData(memEntries, tableVersion);
                return data;
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.AzureTable_60, String.Format(
                    "Intermediate error parsing SiloInstanceTableEntry to MembershipTableData: {0}.", 
                    Utils.EnumerableToString(entries, tuple => tuple.Item1.ToString())), exc);
                throw;
            }
        }

        private static MembershipEntry Parse(SiloInstanceTableEntry tableEntry)
        {
            var parse = new MembershipEntry
            {
                HostName = tableEntry.HostName,
                Status = (SiloStatus) Enum.Parse(typeof (SiloStatus), tableEntry.Status)
            };

            if (!string.IsNullOrEmpty(tableEntry.ProxyPort))
                parse.ProxyPort = int.Parse(tableEntry.ProxyPort);

            int port = 0;
            if (!string.IsNullOrEmpty(tableEntry.Port))
                int.TryParse(tableEntry.Port, out port);

            int gen = 0;
            if (!string.IsNullOrEmpty(tableEntry.Generation))
                int.TryParse(tableEntry.Generation, out gen);

            parse.SiloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Parse(tableEntry.Address), port), gen);

            parse.RoleName = tableEntry.RoleName;
            if (!string.IsNullOrEmpty(tableEntry.SiloName))
            {
                parse.SiloName = tableEntry.SiloName;
            }else if (!string.IsNullOrEmpty(tableEntry.InstanceName))
            {
                // this is for backward compatability: in a mixed cluster of old and new version,
                // some entries will have the old InstanceName column.
                parse.SiloName = tableEntry.InstanceName;
            }
            if (!string.IsNullOrEmpty(tableEntry.UpdateZone))
                parse.UpdateZone = int.Parse(tableEntry.UpdateZone);

            if (!string.IsNullOrEmpty(tableEntry.FaultZone))
                parse.FaultZone = int.Parse(tableEntry.FaultZone);

            parse.StartTime = !string.IsNullOrEmpty(tableEntry.StartTime) ? 
                TraceLogger.ParseDate(tableEntry.StartTime) : default(DateTime);

            parse.IAmAliveTime = !string.IsNullOrEmpty(tableEntry.IAmAliveTime) ?
                TraceLogger.ParseDate(tableEntry.IAmAliveTime) : default(DateTime);

            var suspectingSilos = new List<SiloAddress>();
            var suspectingTimes = new List<DateTime>();

            if (!string.IsNullOrEmpty(tableEntry.SuspectingSilos))
            {
                string[] silos = tableEntry.SuspectingSilos.Split('|');
                foreach (string silo in silos)
                {
                    suspectingSilos.Add(SiloAddress.FromParsableString(silo));
                }
            }

            if (!string.IsNullOrEmpty(tableEntry.SuspectingTimes))
            {
                string[] times = tableEntry.SuspectingTimes.Split('|');
                foreach (string time in times)
                    suspectingTimes.Add(TraceLogger.ParseDate(time));
            }

            if (suspectingSilos.Count != suspectingTimes.Count)
                throw new OrleansException(String.Format("SuspectingSilos.Length of {0} as read from Azure table is not eqaul to SuspectingTimes.Length of {1}", suspectingSilos.Count, suspectingTimes.Count));

            for (int i = 0; i < suspectingSilos.Count; i++)
                parse.AddSuspector(suspectingSilos[i], suspectingTimes[i]);
            
            return parse;
        }

        private static SiloInstanceTableEntry Convert(MembershipEntry memEntry, string deploymentId)
        {
            var tableEntry = new SiloInstanceTableEntry
            {
                DeploymentId = deploymentId,
                Address = memEntry.SiloAddress.Endpoint.Address.ToString(),
                Port = memEntry.SiloAddress.Endpoint.Port.ToString(CultureInfo.InvariantCulture),
                Generation = memEntry.SiloAddress.Generation.ToString(CultureInfo.InvariantCulture),
                HostName = memEntry.HostName,
                Status = memEntry.Status.ToString(),
                ProxyPort = memEntry.ProxyPort.ToString(CultureInfo.InvariantCulture),
                RoleName = memEntry.RoleName,
                SiloName = memEntry.SiloName,
                // this is for backward compatability: in a mixed cluster of old and new version,
                // we need to populate both columns.
                InstanceName = memEntry.SiloName,
                UpdateZone = memEntry.UpdateZone.ToString(CultureInfo.InvariantCulture),
                FaultZone = memEntry.FaultZone.ToString(CultureInfo.InvariantCulture),
                StartTime = TraceLogger.PrintDate(memEntry.StartTime),
                IAmAliveTime = TraceLogger.PrintDate(memEntry.IAmAliveTime)
            };

            if (memEntry.SuspectTimes != null)
            {
                var siloList = new StringBuilder();
                var timeList = new StringBuilder();
                bool first = true;
                foreach (var tuple in memEntry.SuspectTimes)
                {
                    if (!first)
                    {
                        siloList.Append('|');
                        timeList.Append('|');
                    }
                    siloList.Append(tuple.Item1.ToParsableString());
                    timeList.Append(TraceLogger.PrintDate(tuple.Item2));
                    first = false;
                }

                tableEntry.SuspectingSilos = siloList.ToString();
                tableEntry.SuspectingTimes = timeList.ToString();
            }
            else
            {
                tableEntry.SuspectingSilos = String.Empty;
                tableEntry.SuspectingTimes = String.Empty;
            }
            tableEntry.PartitionKey = deploymentId;
            tableEntry.RowKey = SiloInstanceTableEntry.ConstructRowKey(memEntry.SiloAddress);

            return tableEntry;
        }

        private static SiloInstanceTableEntry ConvertPartial(MembershipEntry memEntry, string deploymentId)
        {
            return new SiloInstanceTableEntry
            {
                DeploymentId = deploymentId,
                IAmAliveTime = TraceLogger.PrintDate(memEntry.IAmAliveTime),
                PartitionKey = deploymentId,
                RowKey = SiloInstanceTableEntry.ConstructRowKey(memEntry.SiloAddress)
            };
        }
    }
}
