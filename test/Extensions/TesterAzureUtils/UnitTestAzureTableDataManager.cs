using System;
using System.Text;
using Microsoft.Extensions.Logging;
using Orleans.Internal;
using Orleans.Clustering.AzureStorage;
using Microsoft.Extensions.Logging.Abstractions;
using Azure.Data.Tables;
using Azure;

namespace Tester.AzureUtils
{
    public class UnitTestAzureTableData : ITableEntity
    {
        public byte[] Data { get; set; }
        public string StringData { get; set; }
        public string PartitionKey { get; set; }
        public string RowKey { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }

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
