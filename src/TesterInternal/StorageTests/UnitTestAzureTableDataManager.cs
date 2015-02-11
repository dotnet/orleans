using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Data.Services.Common;
using Microsoft.WindowsAzure.StorageClient;
using Orleans;
using Orleans.AzureUtils;


namespace UnitTests.StorageTests
{
    public class TestConstants
    {
        // Set DataConnectionString to your actual Azure Storage DataConnectionString
        // public static string DataConnectionString ="DefaultEndpointsProtocol=https;AccountName=XXX;AccountKey=YYY"
        // OR:
        // start locval stoarge emulator and UseDevelopmentStorage.
        public static string DataConnectionString = "UseDevelopmentStorage=true";
       
    }


    [Serializable]
    [DataServiceKey("PartitionKey", "RowKey")]
    public class UnitTestAzureTableData : TableServiceEntity
    {
        public byte[] Data { get; set; }
        public string StringData { get; set; }

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("UnitTestAzureData[");
            sb.Append(" PartitionKey=").Append(PartitionKey);
            sb.Append(" RowKey=").Append(RowKey);
            sb.Append(" ]");
            return sb.ToString();
        }
    }

    internal class UnitTestAzureTableDataManager : AzureTableDataManager<UnitTestAzureTableData>
    {
        protected const string INSTANCE_TABLE_NAME = "UnitTestAzureData";

        public UnitTestAzureTableDataManager(string storageConnectionString)
            : base(INSTANCE_TABLE_NAME, storageConnectionString)
        {
            InitTableAsync().WithTimeout(AzureTableDefaultPolicies.TableCreationTimeout).Wait();
        }

        public async Task<IEnumerable<UnitTestAzureTableData>> ReadAllDataAsync(string partitionKey)
        {
            var data = await ReadAllTableEntriesForPartitionAsync(partitionKey)
                .WithTimeout(AzureTableDefaultPolicies.TableCreationTimeout);

            return data.Select(tuple => tuple.Item1);
        }

        public Task<string> WriteDataAsync(string partitionKey, string rowKey, string stringData)
        {
            UnitTestAzureTableData dataObject = new UnitTestAzureTableData();
            dataObject.PartitionKey = partitionKey;
            dataObject.RowKey = rowKey;
            dataObject.StringData = stringData;
            return UpsertTableEntryAsync(dataObject);
        }
    }
}
