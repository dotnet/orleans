using System;
using System.Collections.Generic;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Orleans;
using Orleans.Runtime;
using OrleansAWSUtils.Storage;

namespace UnitTests.StorageTests.AWSUtils
{
    public class AWSTestConstants
    {
        private static readonly Lazy<bool> _isDynamoDbAvailable = new Lazy<bool>(() =>
        {
            try
            {
                DynamoDBStorage storage;
                try
                {
                    storage = new DynamoDBStorage($"Service={Service}", LogManager.GetLogger("DynamoDB"));
                }
                catch (AmazonServiceException)
                {
                    return false;
                }
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

        public static string DefaultSQSConnectionString = "";

        public static string AccessKey { get; set; }
        public static string SecretKey { get; set; }
        public static string Service { get; set; } = "http://localhost:8000";

        public static bool IsDynamoDbAvailable => _isDynamoDbAvailable.Value;
    }
}
