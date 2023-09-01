using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.AWSUtils.Tests;
using Orleans.Internal;
using TestExtensions;

namespace AWSUtils.Tests.StorageTests
{
    public class AWSTestConstants
    {
        private static readonly Lazy<bool> _isDynamoDbAvailable = new Lazy<bool>(() =>
        {
            if (string.IsNullOrEmpty(DynamoDbService))
            {
                return false;
            }

            try
            {
                DynamoDBStorage storage;
                try
                {
                    storage = new DynamoDBStorage(NullLoggerFactory.Instance.CreateLogger("DynamoDBStorage"), DynamoDbService);
                }
                catch (AmazonServiceException)
                {
                    return false;
                }
                storage.InitializeTable(
                    "TestTable",
                    new List<KeySchemaElement> {
                        new KeySchemaElement { AttributeName = "PartitionKey", KeyType = KeyType.HASH }
                    },
                    new List<AttributeDefinition> {
                        new AttributeDefinition { AttributeName = "PartitionKey", AttributeType = ScalarAttributeType.S }
                    })
                .WithTimeout(TimeSpan.FromSeconds(2)).Wait();
                return true;
            }
            catch (Exception exc)
            {
                if (exc.InnerException is TimeoutException)
                    return false;

                throw;
            }
        });

        public static string DynamoDbAccessKey { get; set; } = TestDefaultConfiguration.DynamoDbAccessKey;
        public static string DynamoDbSecretKey { get; set; } = TestDefaultConfiguration.DynamoDbSecretKey;
        public static string DynamoDbService { get; set; } = TestDefaultConfiguration.DynamoDbService;
        public static string SqsConnectionString { get; set; } = TestDefaultConfiguration.SqsConnectionString;

        public static bool IsDynamoDbAvailable => _isDynamoDbAvailable.Value;
        public static bool IsSqsAvailable => !string.IsNullOrWhiteSpace(SqsConnectionString);
    }
}
