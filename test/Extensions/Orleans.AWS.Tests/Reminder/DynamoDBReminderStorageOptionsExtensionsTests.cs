using Orleans.Configuration;
using Xunit;

namespace AWSUtils.Tests.RemindersTest;

[TestSuite("BVT")]
[TestProvider("DynamoDB")]
[TestArea("Reminders")]
[TestCategory("BVT")]
[TestCategory("AWS")]
[TestCategory("DynamoDB")]
[TestCategory("Reminders")]
public sealed class DynamoDBReminderStorageOptionsExtensionsTests
{
    [Fact]
    public void ParseConnectionString_Service_SetsService()
    {
        var options = CreateSentinelOptions();

        options.ParseConnectionString("Service=service-sentinel");

        Assert.Equal("service-sentinel", options.Service);
        Assert.Equal("original-secret", options.SecretKey);
    }

    [Fact]
    public void ParseConnectionString_SecretKey_SetsSecretKey()
    {
        var options = CreateSentinelOptions();

        options.ParseConnectionString("SecretKey=secret-sentinel");

        Assert.Equal("secret-sentinel", options.SecretKey);
        Assert.Equal("original-access", options.AccessKey);
    }

    [Fact]
    public void ParseConnectionString_AccessKey_SetsAccessKey()
    {
        var options = CreateSentinelOptions();

        options.ParseConnectionString("AccessKey=access-sentinel");

        Assert.Equal("access-sentinel", options.AccessKey);
        Assert.Equal("original-secret", options.SecretKey);
    }

    [Fact]
    public void ParseConnectionString_ReadCapacityUnits_SetsReadCapacityUnits()
    {
        var options = CreateSentinelOptions();

        options.ParseConnectionString("ReadCapacityUnits=23");

        Assert.Equal(23, options.ReadCapacityUnits);
        Assert.Equal(29, options.WriteCapacityUnits);
    }

    [Fact]
    public void ParseConnectionString_WriteCapacityUnits_SetsWriteCapacityUnits()
    {
        var options = CreateSentinelOptions();

        options.ParseConnectionString("WriteCapacityUnits=17");

        Assert.Equal(17, options.WriteCapacityUnits);
        Assert.Equal(31, options.ReadCapacityUnits);
    }

    [Fact]
    public void ParseConnectionString_UseProvisionedThroughput_SetsUseProvisionedThroughput()
    {
        var options = CreateSentinelOptions();

        options.ParseConnectionString("UseProvisionedThroughput=true");

        Assert.True(options.UseProvisionedThroughput);
        Assert.False(options.CreateIfNotExists);
    }

    [Fact]
    public void ParseConnectionString_CreateIfNotExists_SetsCreateIfNotExists()
    {
        var options = CreateSentinelOptions();

        options.ParseConnectionString("CreateIfNotExists=true");

        Assert.True(options.CreateIfNotExists);
        Assert.False(options.UpdateIfExists);
    }

    [Fact]
    public void ParseConnectionString_UpdateIfExists_SetsUpdateIfExists()
    {
        var options = CreateSentinelOptions();

        options.ParseConnectionString("UpdateIfExists=true");

        Assert.True(options.UpdateIfExists);
        Assert.False(options.CreateIfNotExists);
    }

    [Fact]
    public void ParseConnectionString_AllSupportedProperties_AreAppliedTogether()
    {
        var options = new DynamoDBReminderStorageOptions();

        options.ParseConnectionString(
            "Service=service-sentinel;" +
            "SecretKey=secret-sentinel;" +
            "AccessKey=access-sentinel;" +
            "ReadCapacityUnits=23;" +
            "WriteCapacityUnits=17;" +
            "UseProvisionedThroughput=false;" +
            "CreateIfNotExists=false;" +
            "UpdateIfExists=false");

        Assert.Equal("service-sentinel", options.Service);
        Assert.Equal("secret-sentinel", options.SecretKey);
        Assert.Equal("access-sentinel", options.AccessKey);
        Assert.Equal(23, options.ReadCapacityUnits);
        Assert.Equal(17, options.WriteCapacityUnits);
        Assert.False(options.UseProvisionedThroughput);
        Assert.False(options.CreateIfNotExists);
        Assert.False(options.UpdateIfExists);
    }

    [Fact]
    public void ParseConnectionString_EmptyInput_PreservesDefaults()
    {
        var options = new DynamoDBReminderStorageOptions();

        options.ParseConnectionString(string.Empty);

        Assert.Null(options.Service);
        Assert.Null(options.SecretKey);
        Assert.Null(options.AccessKey);
        Assert.Equal(10, options.ReadCapacityUnits);
        Assert.Equal(5, options.WriteCapacityUnits);
        Assert.True(options.UseProvisionedThroughput);
        Assert.True(options.CreateIfNotExists);
        Assert.True(options.UpdateIfExists);
        Assert.Equal("OrleansReminders", options.TableName);
    }

    [Theory]
    [InlineData("Service=")]
    [InlineData("SecretKey=")]
    [InlineData("AccessKey=")]
    [InlineData("ReadCapacityUnits=")]
    [InlineData("WriteCapacityUnits=")]
    [InlineData("UseProvisionedThroughput=")]
    [InlineData("CreateIfNotExists=")]
    [InlineData("UpdateIfExists=")]
    [InlineData("Service")]
    [InlineData("=value-without-key")]
    [InlineData("Service=too=many=separators")]
    [InlineData("Service= ")]
    public void ParseConnectionString_MissingOrMalformedSegment_PreservesExistingValues(string connectionString)
    {
        var options = CreateSentinelOptions();

        options.ParseConnectionString(connectionString);

        AssertSentinelOptions(options);
    }

    [Theory]
    [InlineData("ReadCapacityUnits=invalid")]
    [InlineData("WriteCapacityUnits=invalid")]
    public void ParseConnectionString_InvalidInteger_ThrowsFormatException(string connectionString)
    {
        var options = new DynamoDBReminderStorageOptions();

        Assert.Throws<FormatException>(() => options.ParseConnectionString(connectionString));
    }

    [Theory]
    [InlineData("ReadCapacityUnits=2147483648")]
    [InlineData("WriteCapacityUnits=2147483648")]
    public void ParseConnectionString_OverflowingInteger_ThrowsOverflowException(string connectionString)
    {
        var options = new DynamoDBReminderStorageOptions();

        Assert.Throws<OverflowException>(() => options.ParseConnectionString(connectionString));
    }

    [Theory]
    [InlineData("UseProvisionedThroughput=invalid")]
    [InlineData("CreateIfNotExists=invalid")]
    [InlineData("UpdateIfExists=invalid")]
    public void ParseConnectionString_InvalidBoolean_ThrowsFormatException(string connectionString)
    {
        var options = new DynamoDBReminderStorageOptions();

        Assert.Throws<FormatException>(() => options.ParseConnectionString(connectionString));
    }

    private static DynamoDBReminderStorageOptions CreateSentinelOptions() => new()
    {
        Service = "original-service",
        SecretKey = "original-secret",
        AccessKey = "original-access",
        ReadCapacityUnits = 31,
        WriteCapacityUnits = 29,
        UseProvisionedThroughput = false,
        CreateIfNotExists = false,
        UpdateIfExists = false,
        TableName = "original-table",
    };

    private static void AssertSentinelOptions(DynamoDBReminderStorageOptions options)
    {
        Assert.Equal("original-service", options.Service);
        Assert.Equal("original-secret", options.SecretKey);
        Assert.Equal("original-access", options.AccessKey);
        Assert.Equal(31, options.ReadCapacityUnits);
        Assert.Equal(29, options.WriteCapacityUnits);
        Assert.False(options.UseProvisionedThroughput);
        Assert.False(options.CreateIfNotExists);
        Assert.False(options.UpdateIfExists);
        Assert.Equal("original-table", options.TableName);
    }
}
