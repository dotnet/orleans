using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Orleans.Runtime;
using OrleansAWSUtils.Storage;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace Orleans.Providers
{
    public class DynamoDBStatisticsPublisher : IStatisticsPublisher
    {
        private const string PARTITION_KEY_PROPERTY_NAME = "PartitionKey";
        private const string ROW_KEY_PROPERTY_NAME = "RowKey";
        private const string DEPLOYMENT_ID_PROPERTY_NAME = "DeploymentId";
        private const string TIME_PROPERTY_NAME = "Time";
        private const string ADDRESS_PROPERTY_NAME = "Address";
        private const string NAME_PROPERTY_NAME = "Name";
        private const string HOSTNAME_PROPERTY_NAME = "HostName";
        private const string STATISTIC_PROPERTY_NAME = "Statistic";
        private const string STATVALUE_PROPERTY_NAME = "StatValue";
        private const string ISDELTA_PROPERTY_NAME = "IsDelta";
        private const string DATE_TIME_FORMAT = "yyyy-MM-dd-" + "HH:mm:ss.fff 'GMT'";

        private string deploymentId;
        private string address;
        private string name;
        private bool isSilo;
        private long clientEpoch;
        private int counter;
        private string hostName;
        private string tableName;

        private DynamoDBStorage storage;
        private readonly Logger logger;

        public DynamoDBStatisticsPublisher()
        {
            logger = LogManager.GetLogger(this.GetType().Name, LoggerType.Runtime);
        }

        public Task Init(bool isSilo, string storageConnectionString, string deploymentId, string address, string siloName, string hostName)
        {
            this.deploymentId = deploymentId;
            this.address = address;
            name = isSilo ? siloName : hostName;
            this.hostName = hostName;
            this.isSilo = isSilo;
            if (!this.isSilo)
            {
                clientEpoch = SiloAddress.AllocateNewGeneration();
            }
            counter = 0;
            tableName = isSilo ? "OrleansSiloStatistics" : "OrleansClientStatistics";

            storage = new DynamoDBStorage(storageConnectionString, logger);

            return storage.InitializeTable(tableName,
               new List<KeySchemaElement>
               {
                    new KeySchemaElement { AttributeName = PARTITION_KEY_PROPERTY_NAME, KeyType = KeyType.HASH },
                    new KeySchemaElement { AttributeName = ROW_KEY_PROPERTY_NAME, KeyType = KeyType.RANGE }
               },
               new List<AttributeDefinition>
               {
                    new AttributeDefinition { AttributeName = PARTITION_KEY_PROPERTY_NAME, AttributeType = ScalarAttributeType.S },
                    new AttributeDefinition { AttributeName = ROW_KEY_PROPERTY_NAME, AttributeType = ScalarAttributeType.S }
               });
        }

        public Task ReportStats(List<ICounter> statsCounters)
        {
            try
            {
                var toWrite = new List<Dictionary<string, AttributeValue>>();
                foreach (var counter in statsCounters)
                {
                    var fields = ParseCounter(counter);
                    if (fields == null)
                        continue;

                    toWrite.Add(fields);
                }

                if (toWrite.Count <= 25)
                {
                    return storage.DeleteEntriesAsync(tableName, toWrite);
                }
                else
                {
                    var tasks = new List<Task>();
                    foreach (var batch in toWrite.BatchIEnumerable(25))
                    {
                        tasks.Add(storage.DeleteEntriesAsync(tableName, batch));
                    }
                    return Task.WhenAll(tasks);
                }
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.PerfStatistics, string.Format("Unable to write statistics records on table {0} for deploymentId {1}: Exception={2}",
                    tableName, deploymentId, exc));
                throw;
            }
        }

        private Dictionary<string, AttributeValue> ParseCounter(ICounter statsCounter)
        {
            string statValue = statsCounter.IsValueDelta ? statsCounter.GetDeltaString() : statsCounter.GetValueString();
            if ("0".Equals(statValue))
            {
                return null;
            }
            
            counter++;            
            var now = DateTime.UtcNow;
            var ticks = DateTime.MaxValue.Ticks - now.Ticks;

            return new Dictionary<string, AttributeValue>
            {
                // PartitionKey:  DeploymentId$ReverseTimestampToTheNearestHour 
                // RowKey:  ReverseTimestampToTheNearestSecond$Name$counter 
                // As defined in http://dotnet.github.io/orleans/Runtime-Implementation-Details/Runtime-Tables
                { PARTITION_KEY_PROPERTY_NAME, new AttributeValue(string.Join("$", deploymentId, string.Format("{0:d19}", ticks - ticks % TimeSpan.TicksPerHour))) },
                { ROW_KEY_PROPERTY_NAME, new AttributeValue(string.Join("$", string.Format("{0:d19}", ticks), name, string.Format("{0:000000}", counter))) },
                { DEPLOYMENT_ID_PROPERTY_NAME, new AttributeValue(deploymentId) },
                { TIME_PROPERTY_NAME, new AttributeValue(now.ToString(DATE_TIME_FORMAT, CultureInfo.InvariantCulture)) },
                { ADDRESS_PROPERTY_NAME,  new AttributeValue(address)},
                { NAME_PROPERTY_NAME, new AttributeValue(name)},
                { HOSTNAME_PROPERTY_NAME, new AttributeValue(hostName)},
                { STATISTIC_PROPERTY_NAME, new AttributeValue(statsCounter.Name)},
                { ISDELTA_PROPERTY_NAME, new AttributeValue { BOOL = statsCounter.IsValueDelta } },
                { STATVALUE_PROPERTY_NAME, new AttributeValue { N = statValue } }
            };
        }
    }
}
