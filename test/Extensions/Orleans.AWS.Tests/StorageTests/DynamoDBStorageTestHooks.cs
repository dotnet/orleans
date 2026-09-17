using System.Reflection;
using Amazon.DynamoDBv2;
using Amazon.Runtime;

namespace Orleans.AWSUtils.Tests;

internal partial class DynamoDBStorage
{
    private static readonly PropertyInfo ExplicitCredentialsProperty = typeof(AmazonServiceClient)
        .GetProperty("ExplicitAWSCredentials", BindingFlags.Instance | BindingFlags.NonPublic)!;

    internal AmazonDynamoDBClient ClientForTest => _ddbClient;

    internal AWSCredentials? ExplicitCredentialsForTest
        => (AWSCredentials?)ExplicitCredentialsProperty.GetValue(_ddbClient);
}
