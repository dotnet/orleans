using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.AWSUtils.Tests;

namespace AWSUtils.Tests.StorageTests
{
    [Serializable]
    [Orleans.GenerateSerializer]
    internal class UnitTestDynamoDBTableData
    {
        private const string DATA_FIELD = "Data";
        private const string STRING_DATA_FIELD = "StringData";

        [Orleans.Id(0)]
        public string PartitionKey { get; set; }
        [Orleans.Id(1)]
        public string RowKey { get; set; }
        [Orleans.Id(2)]
        public int ETag { get; set; }
        [Orleans.Id(3)]
        public byte[] BinaryData { get; set; }

        [Orleans.Id(4)]
        public string StringData { get; set; }

        public UnitTestDynamoDBTableData()
        {

        }

        public UnitTestDynamoDBTableData(Dictionary<string, AttributeValue> fields)
        {
            if (fields.ContainsKey("PartitionKey"))
            {
                PartitionKey = fields["PartitionKey"].S;
            }

            if (fields.ContainsKey("RowKey"))
            {
                RowKey = fields["RowKey"].S;
            }

            if (fields.ContainsKey("StringData"))
            {
                StringData = fields["StringData"].S;
            }

            if (fields.ContainsKey("ETag"))
            {
                ETag = int.Parse(fields["ETag"].N);
            }

            if (fields.ContainsKey("BinaryData"))
            {
                BinaryData = fields["BinaryData"].B?.ToArray();
            }
        }

        public UnitTestDynamoDBTableData(string data, string partitionKey, string rowKey)
        {
            StringData = data;
            PartitionKey = partitionKey;
            RowKey = rowKey;
        }

        public UnitTestDynamoDBTableData Clone()
        {
            return new UnitTestDynamoDBTableData
            {
                StringData = this.StringData,
                PartitionKey = this.PartitionKey,
                RowKey = this.RowKey
            };
        }

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("UnitTestDDBData[");
            sb.Append(" PartitionKey=").Append(PartitionKey);
            sb.Append(" RowKey=").Append(RowKey);
            sb.Append(" ETag=").Append(ETag);
            sb.Append(" ]");
            return sb.ToString();
        }
    }

    internal class UnitTestDynamoDBStorage : DynamoDBStorage
    {
        public const string INSTANCE_TABLE_NAME = "UnitTestDDBTableData";

        public UnitTestDynamoDBStorage()
            : base(NullLoggerFactory.Instance.CreateLogger("DynamoDBStorage"), AWSTestConstants.DynamoDbService)
        {
            if (AWSTestConstants.IsDynamoDbAvailable)
            {
                InitializeTable(INSTANCE_TABLE_NAME,
                               new List<KeySchemaElement>
                               {
                    new KeySchemaElement { AttributeName = "PartitionKey", KeyType = KeyType.HASH },
                    new KeySchemaElement { AttributeName = "RowKey", KeyType = KeyType.RANGE }
                               },
                               new List<AttributeDefinition>
                               {
                    new AttributeDefinition { AttributeName = "PartitionKey", AttributeType = ScalarAttributeType.S },
                    new AttributeDefinition { AttributeName = "RowKey", AttributeType = ScalarAttributeType.S }
                               }).Wait();
            }
        }
    }
}
