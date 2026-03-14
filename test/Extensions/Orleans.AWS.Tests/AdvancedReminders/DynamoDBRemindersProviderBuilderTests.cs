extern alias AdvancedRemindersDynamoDB;

using System.IO;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Hosting;
using Xunit;
using DynamoDBRemindersProviderBuilder = AdvancedRemindersDynamoDB::Orleans.Hosting.DynamoDBRemindersProviderBuilder;
using AdvancedDynamoDBReminderStorageOptions = AdvancedRemindersDynamoDB::Orleans.Configuration.DynamoDBReminderStorageOptions;

namespace AWSUtils.Tests.AdvancedReminders;

[TestCategory("Reminders"), TestCategory("AWS"), TestCategory("DynamoDb")]
public class DynamoDBRemindersProviderBuilderTests
{
    [Fact]
    public void Configure_BindsTokenAndProfileName()
    {
        const string json = """
        {
          "Orleans": {
            "Reminders": {
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
        var providerBuilder = new DynamoDBRemindersProviderBuilder();

        providerBuilder.Configure(siloBuilder, "DynamoDB", siloBuilder.Configuration.GetSection("Orleans:Reminders:DynamoDB"));

        var options = siloBuilder.Services.BuildServiceProvider()
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<AdvancedDynamoDBReminderStorageOptions>>()
            .Value;

        Assert.Equal("access", options.AccessKey);
        Assert.Equal("secret", options.SecretKey);
        Assert.Equal("eu-west-1", options.Service);
        Assert.Equal("session-token", options.Token);
        Assert.Equal("dev-profile", options.ProfileName);
        Assert.Equal("AdvancedReminders", options.TableName);
    }

    private sealed class TestSiloBuilder(string json) : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();
    }
}
