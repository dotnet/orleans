using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage.Table;
using Orleans.Runtime;


namespace Orleans.AzureUtils
{
    [Serializable]
    internal class StatsTableData: TableEntity
    {
        public string DeploymentId { get; set; }
        public string Time { get; set; }
        public string Address { get; set; }
        public string Name { get; set; }
        public string HostName { get; set; }

        public string Statistic { get; set; }
        public string StatValue { get; set; }
        public bool IsDelta { get; set; }

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("StatsTableData[");
            sb.Append(" PartitionKey=").Append(PartitionKey);
            sb.Append(" RowKey=").Append(RowKey);

            sb.Append(" DeploymentId=").Append(DeploymentId);
            sb.Append(" Time=").Append(Time);
            sb.Append(" Address=").Append(Address);
            sb.Append(" Name=").Append(Name);
            sb.Append(" HostName=").Append(HostName);
            sb.Append(" Statistic=").Append(Statistic);
            sb.Append(" StatValue=").Append(StatValue);
            sb.Append(" IsDelta=").Append(IsDelta);
            sb.Append(" ]");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Publishes silo or client statistics to Azure Table
    /// </summary>
    public class StatsTableDataManager : IStatisticsPublisher
    {
        private string deploymentId;
        private string address;
        private string name;
        private bool isSilo;
        private long clientEpoch;
        private int counter;
        private string myHostName;
        private const string DATE_TIME_FORMAT = "yyyy-MM-dd-" + "HH:mm:ss.fff 'GMT'"; // Example: 2010-09-02 09:50:43.341 GMT - Variant of UniversalSorta­bleDateTimePat­tern


        private AzureTableDataManager<StatsTableData> tableManager;
        private readonly Logger logger;

        private static readonly TimeSpan initTimeout = AzureTableDefaultPolicies.TableCreationTimeout;

        private StatsTableDataManager()
        {
            logger = LogManager.GetLogger(this.GetType().Name, LoggerType.Runtime);
            
        }

        async Task IStatisticsPublisher.Init(bool isSilo, string storageConnectionString, string deploymentId, string address, string siloName, string hostName)
        {
            this.deploymentId = deploymentId;
            this.address = address;
            name = siloName;
            myHostName = hostName;
            this.isSilo = isSilo;
            if (!this.isSilo)
            {
                clientEpoch = SiloAddress.AllocateNewGeneration();
            }
            counter = 0;
            var tableName = isSilo ? "OrleansSiloStatistics" : "OrleansClientStatistics";
            tableManager = new AzureTableDataManager<StatsTableData>(tableName, storageConnectionString, logger);
            await tableManager.InitTableAsync().WithTimeout(initTimeout);
        }

        /// <summary>
        /// Writes a set of statistics to storage.
        /// </summary>
        /// <param name="statsCounters">Statistics to write</param>
        /// <returns></returns>
        public Task ReportStats(List<ICounter> statsCounters)
        {
            var bulkPromises = new List<Task>();
            var data = new List<StatsTableData>();
            foreach (ICounter count in statsCounters.Where(cs => cs.Storage == CounterStorage.LogAndTable).OrderBy(cs => cs.Name))
            {
                if (data.Count >= AzureTableDefaultPolicies.MAX_BULK_UPDATE_ROWS)
                {
                    // Write intermediate batch
                    bulkPromises.Add(tableManager.BulkInsertTableEntries(data));
                    data.Clear();
                }

                StatsTableData statsTableEntry = PopulateStatsTableDataEntry(count);
                if (statsTableEntry == null) continue; // Skip blank entries
                if (logger.IsVerbose2) logger.Verbose2("Preparing to bulk insert {1} stats table entry: {0}", statsTableEntry, isSilo ? "silo" : "");
                data.Add(statsTableEntry);
            }
            if (data.Count > 0)
            {
                // Write final batch
                bulkPromises.Add(tableManager.BulkInsertTableEntries(data));
            }
            return Task.WhenAll(bulkPromises);
        }

        private StatsTableData PopulateStatsTableDataEntry(ICounter statsCounter)
        {
            string statValue = statsCounter.IsValueDelta ? statsCounter.GetDeltaString() : statsCounter.GetValueString();
            if ("0".Equals(statValue))
            {
                // Skip writing empty records
                return null;
            }

            counter++;
            var entry = new StatsTableData { StatValue = statValue };
            
            // We store the statistics grouped by an hour in the same partition and sorted by reverse ticks within the partition.
            // Since by default Azure table stores Entities in ascending order based on the Row Key - if we just used the current ticks
            // it would return the oldest entry first.
            // If we store the rows in the reverse order (some max value - current ticks), it will return the latest most recent entry first.
            // More details here:
            // http://gauravmantri.com/2012/02/17/effective-way-of-fetching-diagnostics-data-from-windows-azure-diagnostics-table-hint-use-partitionkey/
            // https://alexandrebrisebois.wordpress.com/2014/06/16/using-time-based-partition-keys-in-azure-table-storage/
            // https://stackoverflow.com/questions/1004698/how-to-truncate-milliseconds-off-of-a-net-datetime
            
            // Our format:
            // PartitionKey:  DeploymentId$ReverseTimestampToTheNearestHour 
            // RowKey:  ReverseTimestampToTheNearestSecond$Name$counter 

            var now = DateTime.UtcNow;
            // number of ticks remaining until the year 9683
            var ticks = DateTime.MaxValue.Ticks - now.Ticks;

            // partition the table according to the deployment id and hour
            entry.PartitionKey = string.Join("$", deploymentId, string.Format("{0:d19}", ticks - ticks % TimeSpan.TicksPerHour));
            var counterStr = String.Format("{0:000000}", counter);
            
            // order the rows latest-first in the table 
            entry.RowKey = string.Join("$", string.Format("{0:d19}", ticks), name, counterStr);
            entry.DeploymentId = deploymentId;
            entry.Time = now.ToString(DATE_TIME_FORMAT, CultureInfo.InvariantCulture); ;
            entry.Address = address;
            entry.Name = name;
            entry.HostName = myHostName;
            entry.Statistic = statsCounter.Name;
            entry.IsDelta = statsCounter.IsValueDelta;
            return entry;
        }
    }
}
