using System.IO;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.AdvancedReminders.DynamoDB;
using Orleans.Hosting;
using Xunit;

namespace AWSUtils.Tests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders"), TestCategory("AWS"), TestCategory("DynamoDb")]
public class DynamoDBRemindersProviderBuilderTests
{
    [Fact]
    public void Configure_BindsTokenAndProfileName()
    {
        const string json = """
        {
          "Orleans": {
            "AdvancedReminders": {
              "DynamoDB": {
                "ProviderType": "DynamoDB",
                "AccessKey": "access",
                "SecretKey": "secret",
                "Service": "eu-west-1",
                "Token": "session-token",
                "ProfileName": "dev-profile",
                "TableName": "AdvancedReminders"
              }
            }
          }
        }
        """;

        var siloBuilder = new TestSiloBuilder(json);
        var providerBuilder = new AdvancedDynamoDBRemindersProviderBuilder();

        providerBuilder.Configure(siloBuilder, "DynamoDB", siloBuilder.Configuration.GetSection("Orleans:AdvancedReminders:DynamoDB"));

        var options = siloBuilder.Services.BuildServiceProvider()
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<DynamoDBReminderStorageOptions>>()
            .Value;

        Assert.Equal("access", options.AccessKey);
        Assert.Equal("secret", options.SecretKey);
        Assert.Equal("eu-west-1", options.Service);
        Assert.Equal("session-token", options.Token);
        Assert.Equal("dev-profile", options.ProfileName);
        Assert.Equal("AdvancedReminders", options.TableName);
    }

    [Theory]
    [InlineData("OnDemand")]
    [InlineData("Provisioned(7,9)")]
    public void Configure_BindsOneCapacityPolicy(string configured)
    {
        var siloBuilder = new TestSiloBuilder($$"""
        { "Settings": { "ProviderType": "DynamoDB", "Capacity": "{{configured}}" } }
        """);
        new AdvancedDynamoDBRemindersProviderBuilder().Configure(siloBuilder, "DynamoDB", siloBuilder.Configuration.GetSection("Settings"));
        using var provider = siloBuilder.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<DynamoDBReminderStorageOptions>>().Value;

        Assert.Equal(configured, options.Capacity.ToString());
    }

    [Theory]
    [InlineData("ReadCapacityUnits", "7")]
    [InlineData("WriteCapacityUnits", "9")]
    [InlineData("UseProvisionedThroughput", "false")]
    public void Configure_RejectsLegacyCapacityKeys(string key, string value)
    {
        var siloBuilder = new TestSiloBuilder($$"""
        { "Settings": { "ProviderType": "DynamoDB", "{{key}}": "{{value}}" } }
        """);
        new AdvancedDynamoDBRemindersProviderBuilder().Configure(siloBuilder, "DynamoDB", siloBuilder.Configuration.GetSection("Settings"));
        using var provider = siloBuilder.Services.BuildServiceProvider();

        Assert.Throws<OrleansConfigurationException>(() =>
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<DynamoDBReminderStorageOptions>>().Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Provisioned(0,5)")]
    [InlineData("OnDemand(1,2)")]
    public void Configure_RejectsMissingOrInvalidPolicyValue(string? configured)
    {
        var value = System.Text.Json.JsonSerializer.Serialize(configured);
        var siloBuilder = new TestSiloBuilder($$"""
        { "Settings": { "Capacity": {{value}} } }
        """);
        new AdvancedDynamoDBRemindersProviderBuilder().Configure(siloBuilder, "DynamoDB", siloBuilder.Configuration.GetSection("Settings"));
        using var provider = siloBuilder.Services.BuildServiceProvider();

        if (string.IsNullOrWhiteSpace(configured))
        {
            Assert.Throws<OrleansConfigurationException>(() =>
                provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<DynamoDBReminderStorageOptions>>().Value);
        }
        else
        {
            Assert.Throws<FormatException>(() =>
                provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<DynamoDBReminderStorageOptions>>().Value);
        }
    }

    private sealed class TestSiloBuilder(string json) : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();
    }
}
