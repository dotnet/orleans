using Amazon;
using System;

#if CLUSTERING_DYNAMODB
namespace Orleans.Clustering.DynamoDB
#elif PERSISTENCE_DYNAMODB
namespace Orleans.Persistence.DynamoDB
#elif REMINDERS_DYNAMODB
namespace Orleans.Reminders.DynamoDB
#elif STREAMING_SQS
namespace Orleans.Streaming.SQS
#elif AWSUTILS_TESTS
namespace Orleans.AWSUtils.Tests
#elif TRANSACTIONS_DYNAMODB
namespace Orleans.Transactions.DynamoDB
#else
// No default namespace intentionally to cause compile errors if something is not defined
#endif
{
    /// <summary>
    /// Some basic utilities methods for AWS SDK
    /// </summary>
    internal static class AWSUtils
    {
        internal static RegionEndpoint GetRegionEndpoint(string zone = "")
        {
            //
            // Keep the order from RegionEndpoint so it is easier to maintain.
            // us-west-2 is the default
            //

            return RegionEndpoint.GetBySystemName(zone) ?? RegionEndpoint.USWest2;
        }

        /// <summary>
        /// Validate DynamoDB PartitionKey.
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        public static string ValidateDynamoDBPartitionKey(string key)
        {
            if (key.Length >= 2048)
                throw new ArgumentException(string.Format("Key length {0} is too long to be an DynamoDB partition key. Key={1}", key.Length, key));

            return key;
        }

        /// <summary>
        /// Validate DynamoDB RowKey.
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        public static string ValidateDynamoDBRowKey(string key)
        {
            if (key.Length >= 1024)
                throw new ArgumentException(string.Format("Key length {0} is too long to be an DynamoDB row key. Key={1}", key.Length, key));

            return key;
        }
    }
}
