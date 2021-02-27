using System;
using System.Text;
using Microsoft.Azure.Cosmos.Table;
using Microsoft.Extensions.Logging;
using Orleans.Internal;
using Orleans.Clustering.AzureStorage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tester.AzureUtils
{
    [Serializable]
    [Orleans.GenerateSerializer]
    public class UnitTestAzureTableData : TableEntity
    {
        [Orleans.Id(0)]
        public byte[] Data { get; set; }
        [Orleans.Id(1)]
        public string StringData { get; set; }

        public UnitTestAzureTableData()
        {

        }

        public UnitTestAzureTableData(string data, string partitionKey, string rowKey)
        {
            StringData = data;
            PartitionKey = partitionKey;
            RowKey = rowKey;
        }

        public UnitTestAzureTableData Clone()
        {
            return new UnitTestAzureTableData
            {
                StringData = this.StringData,
                PartitionKey = this.PartitionKey,
                RowKey = this.RowKey
            };
        }

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

        public UnitTestAzureTableDataManager()
            : base(new AzureStorageOperationOptions { TableName = INSTANCE_TABLE_NAME }.ConfigureTestDefaults(),
                  NullLoggerFactory.Instance.CreateLogger<UnitTestAzureTableDataManager>())
        {
            InitTableAsync().WithTimeout(new AzureStoragePolicyOptions().CreationTimeout).Wait();
        }
    }
}
