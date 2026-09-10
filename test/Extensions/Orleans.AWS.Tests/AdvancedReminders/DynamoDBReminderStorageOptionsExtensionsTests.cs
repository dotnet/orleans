using Orleans.AdvancedReminders.DynamoDB;
using Xunit;

namespace AWSUtils.Tests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders"), TestCategory("AWS"), TestCategory("DynamoDb")]
public class DynamoDBReminderStorageOptionsExtensionsTests
{
    [Fact]
    public void ParseConnectionString_MatchesOnlyCaseInsensitiveKeyPrefixes()
    {
        const string connectionString =
            "Ignored=Service;service=eu-west-1;" +
            "Ignored=SecretKey;secretkey=secret;" +
            "Ignored=AccessKey;accesskey=access;" +
            "Ignored=Token;token=session-token;" +
            "Ignored=Capacity;capacity=Provisioned(7,9);" +
            "Ignored=CreateIfNotExists;createifnotexists=false;" +
            "Ignored=UpdateIfExists;updateifexists=false";
        var options = new DynamoDBReminderStorageOptions();

        options.ParseConnectionString(connectionString);

        Assert.Equal("eu-west-1", options.Service);
        Assert.Equal("secret", options.SecretKey);
        Assert.Equal("access", options.AccessKey);
        Assert.Equal("session-token", options.Token);
        Assert.Equal("Provisioned(7,9)", options.Capacity.ToString());
        Assert.False(options.CreateIfNotExists);
        Assert.False(options.UpdateIfExists);
    }

    [Fact]
    public void ParseConnectionString_PreservesEqualsCharactersInCredentials()
    {
        const string secretKey = "c2VjcmV0PT0=";
        const string accessKey = "YWNjZXNzPQ==";
        const string token = "dG9rZW49PQ==";
        var options = new DynamoDBReminderStorageOptions();

        options.ParseConnectionString($"SecretKey={secretKey};AccessKey={accessKey};Token={token}");

        Assert.Equal(secretKey, options.SecretKey);
        Assert.Equal(accessKey, options.AccessKey);
        Assert.Equal(token, options.Token);
    }

    [Fact]
    public void ParseConnectionString_SelectsOnDemandWithoutProvisionedValues()
    {
        var options = new DynamoDBReminderStorageOptions();

        options.ParseConnectionString("Capacity=OnDemand");

        Assert.Same(DynamoDBCapacity.OnDemand, options.Capacity);
    }

    [Theory]
    [InlineData("ReadCapacityUnits=7")]
    [InlineData("writecapacityunits=9")]
    [InlineData("UseProvisionedThroughput=false")]
    public void ParseConnectionString_RejectsLegacyCapacityKeys(string configured)
    {
        var options = new DynamoDBReminderStorageOptions();

        var exception = Assert.Throws<OrleansConfigurationException>(() => options.ParseConnectionString(configured));

        Assert.Contains("Capacity=", exception.Message);
    }
}
