using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Azure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.AzureUtils;
using Orleans.Clustering.AzureStorage;
using Orleans.Clustering.AzureStorage.Utilities;
using Orleans.Configuration;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Orleans.Runtime.MembershipService
{
    internal class AzureBasedMembershipTable : IMembershipTable
    {
        private readonly ILogger logger;
        private readonly ILoggerFactory loggerFactory;
        private OrleansSiloInstanceManager tableManager;
        private readonly AzureStorageClusteringOptions options;
        private readonly string clusterId;

        public AzureBasedMembershipTable(
            ILoggerFactory loggerFactory,
            IOptions<AzureStorageClusteringOptions> clusteringOptions,
            IOptions<ClusterOptions> clusterOptions)
        {
            this.loggerFactory = loggerFactory;
            this.logger = loggerFactory.CreateLogger<AzureBasedMembershipTable>();
            this.options = clusteringOptions.Value;
            this.clusterId = clusterOptions.Value.ClusterId;
        }

        public async Task InitializeMembershipTable(bool tryInitTableVersion)
        {
            LogFormatter.SetExceptionDecoder(typeof(RequestFailedException), AzureTableUtils.PrintStorageException);

            this.tableManager = await OrleansSiloInstanceManager.GetManager(
                this.clusterId,
                this.loggerFactory,
                this.options);

            // even if I am not the one who created the table,
            // try to insert an initial table version if it is not already there,
            // so we always have a first table version row, before this silo starts working.
            if (tryInitTableVersion)
            {
                // ignore return value, since we don't care if I inserted it or not, as long as it is in there.
                bool created = await tableManager.TryCreateTableVersionEntryAsync();
                if(created) logger.LogInformation("Created new table version row.");
            }
        }

        public Task DeleteMembershipTableEntries(string clusterId)
        {
            return tableManager.DeleteTableEntries(clusterId);
        }

        public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate)
        {
            return tableManager.CleanupDefunctSiloEntries(beforeDate);
        }

        public async Task<MembershipTableData> ReadRow(SiloAddress key)
        {
            try
            {
                var entries = await tableManager.FindSiloEntryAndTableVersionRow(key);
                MembershipTableData data = Convert(entries);
                if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug($"Read my entry {{SiloAddress}} Table={Environment.NewLine}{{Data}}", key.ToString(), data.ToString());
                return data;
            }
            catch (Exception exc)
            {
                logger.LogWarning((int)TableStorageErrorCode.AzureTable_20,
                    exc,
                    "Intermediate error reading silo entry for key {SiloAddress} from the table {TableName}.", key.ToString(), tableManager.TableName);
                throw;
            }
        }

        public async Task<MembershipTableData> ReadAll()
        {
            try
            {
                var entries = await tableManager.FindAllSiloEntries();
                MembershipTableData data = Convert(entries);
                if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace($"ReadAll Table={Environment.NewLine}{{Data}}", data.ToString());

                return data;
            }
            catch (Exception exc)
            {
                logger.LogWarning((int)TableStorageErrorCode.AzureTable_21,
                    exc,
                    "Intermediate error reading all silo entries {TableName}.", tableManager.TableName);
                throw;
            }
        }

        public async Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion)
        {
            try
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("InsertRow entry = {Data}, table version = {TableVersion}", entry.ToString(), tableVersion);
                var tableEntry = Convert(entry, tableManager.DeploymentId);
                var versionEntry = tableManager.CreateTableVersionEntry(tableVersion.Version);

                bool result = await tableManager.InsertSiloEntryConditionally(
                    tableEntry, versionEntry, tableVersion.VersionEtag);

                if (result == false)
                    logger.LogWarning((int)TableStorageErrorCode.AzureTable_22,
                        "Insert failed due to contention on the table. Will retry. Entry {Data}, table version = {TableVersion}", entry.ToString(), tableVersion);
                return result;
            }
            catch (Exception exc)
            {
                logger.LogWarning((int)TableStorageErrorCode.AzureTable_23,
                    exc,
                    "Intermediate error inserting entry {Data} tableVersion {TableVersion} to the table {TableName}.", entry.ToString(), tableVersion is null ? "null" : tableVersion.ToString(), tableManager.TableName);
                throw;
            }
        }

        public async Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
        {
            try
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("UpdateRow entry = {Data}, etag = {ETag}, table version = {TableVersion}", entry.ToString(), etag, tableVersion);
                var siloEntry = Convert(entry, tableManager.DeploymentId);
                var versionEntry = tableManager.CreateTableVersionEntry(tableVersion.Version);

                bool result = await tableManager.UpdateSiloEntryConditionally(siloEntry, etag, versionEntry, tableVersion.VersionEtag);
                if (result == false)
                    logger.LogWarning(
                        (int)TableStorageErrorCode.AzureTable_24,
                        "Update failed due to contention on the table. Will retry. Entry {Data}, eTag {ETag}, table version = {TableVersion}",
                        entry.ToString(),
                        etag,
                        tableVersion);
                return result;
            }
            catch (Exception exc)
            {
                logger.LogWarning((int)TableStorageErrorCode.AzureTable_25,
                    exc,
                    "Intermediate error updating entry {Data} tableVersion {TableVersion} to the table {TableName}.", entry.ToString(), tableVersion is null ? "null" : tableVersion.ToString(), tableManager.TableName);
                throw;
            }
        }

        public async Task UpdateIAmAlive(MembershipEntry entry)
        {
            try
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Merge entry = {Data}", entry.ToString());
                var siloEntry = ConvertPartial(entry, tableManager.DeploymentId);
                await tableManager.MergeTableEntryAsync(siloEntry);
            }
            catch (Exception exc)
            {
                logger.LogWarning((int)TableStorageErrorCode.AzureTable_26,
                    exc,
                    "Intermediate error updating IAmAlive field for entry {Data} to the table {TableName}.", entry.ToString(), tableManager.TableName);
                throw;
            }
        }

        private MembershipTableData Convert(List<(SiloInstanceTableEntry Entity, string ETag)> entries)
        {
            try
            {
                var memEntries = new List<Tuple<MembershipEntry, string>>();
                TableVersion tableVersion = null;
                foreach (var tuple in entries)
                {
                    var tableEntry = tuple.Entity;
                    if (tableEntry.RowKey.Equals(SiloInstanceTableEntry.TABLE_VERSION_ROW))
                    {
                        tableVersion = new TableVersion(int.Parse(tableEntry.MembershipVersion), tuple.ETag);
                    }
                    else
                    {
                        try
                        {
                            
                            MembershipEntry membershipEntry = Parse(tableEntry);
                            memEntries.Add(new Tuple<MembershipEntry, string>(membershipEntry, tuple.ETag));
                        }
                        catch (Exception exc)
                        {
                            logger.LogError((int)TableStorageErrorCode.AzureTable_61,
                                exc,
                                "Intermediate error parsing SiloInstanceTableEntry to MembershipTableData: {Data}. Ignoring this entry.", tableEntry);
                        }
                    }
                }
                var data = new MembershipTableData(memEntries, tableVersion);
                return data;
            }
            catch (Exception exc)
            {
                logger.LogError((int)TableStorageErrorCode.AzureTable_60,
                    exc,
                    "Intermediate error parsing SiloInstanceTableEntry to MembershipTableData: {Data}.", Utils.EnumerableToString(entries, tuple => tuple.Entity.ToString()));
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

            parse.SiloAddress = SiloAddress.New(IPAddress.Parse(tableEntry.Address), port, gen);

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
                LogFormatter.ParseDate(tableEntry.StartTime) : default;

            parse.IAmAliveTime = !string.IsNullOrEmpty(tableEntry.IAmAliveTime) ?
                LogFormatter.ParseDate(tableEntry.IAmAliveTime) : default;

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
                    suspectingTimes.Add(LogFormatter.ParseDate(time));
            }

            if (suspectingSilos.Count != suspectingTimes.Count)
                throw new OrleansException(string.Format("SuspectingSilos.Length of {0} as read from Azure table is not equal to SuspectingTimes.Length of {1}", suspectingSilos.Count, suspectingTimes.Count));

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
                StartTime = LogFormatter.PrintDate(memEntry.StartTime),
                IAmAliveTime = LogFormatter.PrintDate(memEntry.IAmAliveTime)
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
                    timeList.Append(LogFormatter.PrintDate(tuple.Item2));
                    first = false;
                }

                tableEntry.SuspectingSilos = siloList.ToString();
                tableEntry.SuspectingTimes = timeList.ToString();
            }
            else
            {
                tableEntry.SuspectingSilos = string.Empty;
                tableEntry.SuspectingTimes = string.Empty;
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
                IAmAliveTime = LogFormatter.PrintDate(memEntry.IAmAliveTime),
                PartitionKey = deploymentId,
                RowKey = SiloInstanceTableEntry.ConstructRowKey(memEntry.SiloAddress)
            };
        }
    }
}
