using Amazon.DynamoDBv2;
using Amazon.Runtime;

namespace Orleans.AWSUtils.Tests;

internal partial class DynamoDBStorage
{
    internal AmazonDynamoDBClient ClientForTest => _ddbClient;

    internal AWSCredentials? GetExplicitCredentialsForTest() => GetExplicitCredentials();
}
