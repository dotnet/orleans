using Amazon.DynamoDBv2.Model;
using Orleans.Runtime;
using OrleansAWSUtils.Storage;
using System;
using System.Collections.Generic;
using Orleans;
using Amazon.DynamoDBv2;

namespace UnitTests.StorageTests.AWSUtils
{
    public class AWSTestConstants
    {
        public static string DefaultSQSConnectionString = "";

        public static string AccessKey { get; set; }
        public static string SecretKey { get; set; }
        public static string Service { get; set; } = "http://localhost:8000";

        public static Lazy<bool> CanConnectDynamoDb = new Lazy<bool>(() =>
        {
            var storage = new DynamoDBStorage($"Service={Service}", LogManager.GetLogger("DynamoDB"));
            try
            {
                storage.InitializeTable("TestTable", new List<KeySchemaElement> {
                    new KeySchemaElement { AttributeName = "PartitionKey", KeyType = KeyType.HASH }
                }, new List<AttributeDefinition> {
                    new AttributeDefinition { AttributeName = "PartitionKey", AttributeType = ScalarAttributeType.S }
                }).WithTimeout(TimeSpan.FromSeconds(2)).Wait();
                return true;
            }
            catch (Exception exc)
            {
                if (exc.InnerException is TimeoutException)
                    return false;

                throw;
            }            
        });
    }
}
