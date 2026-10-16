using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
using Xunit;

namespace AWSUtils.Tests.RemindersTest;

[TestSuite("BVT")]
[TestProvider("DynamoDB")]
[TestArea("Reminders")]
[TestCategory("BVT")]
[TestCategory("AWS")]
[TestCategory("DynamoDB")]
[TestCategory("Reminders")]
public sealed class DynamoDBRemindersProviderBuilderTests
{
    [Theory]
    [InlineData("token-sentinel", "profile-sentinel")]
    [InlineData(null, null)]
    public void Configure_CredentialKeysBindIndependently(string? token, string? profileName)
    {
        var values = new Dictionary<string, string?>
        {
            [$"Reminders:{nameof(DynamoDBReminderStorageOptions.SecretKey)}"] = "secret-sentinel",
            [$"Reminders:{nameof(DynamoDBReminderStorageOptions.Token)}"] = token,
            [$"Reminders:{nameof(DynamoDBReminderStorageOptions.ProfileName)}"] = profileName,
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values.Where(setting => setting.Value is not null))
            .Build();
        var builder = new TestSiloBuilder(configuration);
        IProviderBuilder<ISiloBuilder> provider = new DynamoDBRemindersProviderBuilder();

        provider.Configure(builder, null, configuration.GetSection("Reminders"));

        using var services = builder.Services.BuildServiceProvider();
        var options = services.GetRequiredService<IOptions<DynamoDBReminderStorageOptions>>().Value;
        Assert.Equal("secret-sentinel", options.SecretKey);
        Assert.Equal(token, options.Token);
        Assert.Equal(profileName, options.ProfileName);
    }

    [Theory]
    [InlineData(nameof(DynamoDBReminderStorageOptions.ReadCapacityUnits), "not-an-integer", typeof(int))]
    [InlineData(nameof(DynamoDBReminderStorageOptions.WriteCapacityUnits), "not-an-integer", typeof(int))]
    [InlineData(nameof(DynamoDBReminderStorageOptions.UseProvisionedThroughput), "not-a-boolean", typeof(bool))]
    [InlineData(nameof(DynamoDBReminderStorageOptions.CreateIfNotExists), "not-a-boolean", typeof(bool))]
    [InlineData(nameof(DynamoDBReminderStorageOptions.UpdateIfExists), "not-a-boolean", typeof(bool))]
    public void Configure_InvalidTypedValue_Throws(string key, string invalidValue, Type expectedType)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new($"Reminders:{key}", invalidValue)])
            .Build();
        var builder = new TestSiloBuilder(configuration);
        IProviderBuilder<ISiloBuilder> provider = new DynamoDBRemindersProviderBuilder();

        provider.Configure(builder, null, configuration.GetSection("Reminders"));

        using var services = builder.Services.BuildServiceProvider();
        var exception = Assert.Throws<InvalidOperationException>(
            () => services.GetRequiredService<IOptions<DynamoDBReminderStorageOptions>>().Value);
        Assert.Equal($"Failed to convert configuration value at 'Reminders:{key}' to type '{expectedType}'.", exception.Message);
    }

    private sealed class TestSiloBuilder(IConfiguration configuration) : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = configuration;
    }
}
