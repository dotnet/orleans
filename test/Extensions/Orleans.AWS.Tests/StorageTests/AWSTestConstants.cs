using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
#if TRANSACTIONS_DYNAMODB_TESTS
using Orleans.Transactions.DynamoDB;
#else
using Orleans.AWSUtils.Tests;
#endif
using Orleans.Internal;
using TestExtensions;

namespace AWSUtils.Tests.StorageTests
{
    public class AWSTestConstants
    {
        private static readonly Lazy<bool> _isDynamoDbAvailable = new(
            () =>
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
                    .WaitAsync(TimeSpan.FromSeconds(2), Xunit.TestContext.Current.CancellationToken)
                    .GetAwaiter()
                    .GetResult();
                    return true;
                }
                catch (TimeoutException)
                {
                    return false;
                }
                catch (Exception exc)
                {
                    if (exc.InnerException is TimeoutException)
                        return false;

                    throw;
                }
            },
            LazyThreadSafetyMode.PublicationOnly);

        public static string DynamoDbAccessKey { get; set; } = TestDefaultConfiguration.DynamoDbAccessKey!;
        public static string DynamoDbSecretKey { get; set; } = TestDefaultConfiguration.DynamoDbSecretKey!;
        public static string DynamoDbService { get; set; } = TestDefaultConfiguration.DynamoDbService!;
        public static string SqsConnectionString { get; set; } = TestDefaultConfiguration.SqsConnectionString!;

        public static bool IsDynamoDbAvailable => _isDynamoDbAvailable.Value;
        public static bool IsSqsAvailable => !string.IsNullOrWhiteSpace(SqsConnectionString);
    }
}
