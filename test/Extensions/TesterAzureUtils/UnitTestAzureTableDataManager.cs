using System;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage.Table;
using Orleans;
using Orleans.Tests.AzureUtils;

namespace Tester.AzureUtils
{
    [Serializable]    
    public class UnitTestAzureTableData : TableEntity
    {
        public byte[] Data { get; set; }
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

        public UnitTestAzureTableDataManager(string storageConnectionString, ILoggerFactory loggerFactory)
            : base(INSTANCE_TABLE_NAME, storageConnectionString, loggerFactory)
        {
            InitTableAsync().WithTimeout(AzureTableDefaultPolicies.TableCreationTimeout).Wait();
        }
    }
}
