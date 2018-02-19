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

            switch (zone)
            {
                case "us-east-1":
                    return RegionEndpoint.USEast1;
                case "ca-central-1":
                    return RegionEndpoint.CACentral1;
                case "cn-north-1":
                    return RegionEndpoint.CNNorth1;
                case "us-gov-west-1":
                    return RegionEndpoint.USGovCloudWest1;
                case "sa-east-1":
                    return RegionEndpoint.SAEast1;
                case "ap-southeast-1":
                    return RegionEndpoint.APSoutheast1;
                case "ap-south-1":
                    return RegionEndpoint.APSouth1;
                case "ap-northeast-2":
                    return RegionEndpoint.APNortheast2;
                case "ap-southeast-2":
                    return RegionEndpoint.APSoutheast2;
                case "eu-central-1":
                    return RegionEndpoint.EUCentral1;
                case "eu-west-2":
                    return RegionEndpoint.EUWest2;
                case "eu-west-1":
                    return RegionEndpoint.EUWest1;
                case "us-west-1":
                    return RegionEndpoint.USWest1;
                case "us-east-2":
                    return RegionEndpoint.USEast2;
                case "ap-northeast-1":
                    return RegionEndpoint.APNortheast1;
                default:
                    return RegionEndpoint.USWest2;
            }
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
