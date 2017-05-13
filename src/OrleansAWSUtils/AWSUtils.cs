using Amazon;
using System;

namespace OrleansAWSUtils
{
    /// <summary>
    /// Some basic utilities methods for AWS SDK
    /// </summary>
    internal static class AWSUtils
    {
        internal static RegionEndpoint GetRegionEndpoint(string zone = "")
        {
            switch (zone)
            {
                case "us-east-1":
                    return RegionEndpoint.USEast1;
                case "us-west-1":
                    return RegionEndpoint.USWest1;
                case "ap-south-1":
                    return RegionEndpoint.APSouth1;
                case "ap-northeast-2":
                    return RegionEndpoint.APNortheast2;
                case "ap-southeast-1":
                    return RegionEndpoint.APSoutheast1;
                case "ap-southeast-2":
                    return RegionEndpoint.APSoutheast2;
                case "ap-northeast-1":
                    return RegionEndpoint.APNortheast1;
                case "eu-central-1":
                    return RegionEndpoint.EUCentral1;
                case "eu-west-1":
                    return RegionEndpoint.EUWest1;
                case "sa-east-1":
                    return RegionEndpoint.SAEast1;
                case "us-gov-west-1":
                    return RegionEndpoint.USGovCloudWest1;
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
