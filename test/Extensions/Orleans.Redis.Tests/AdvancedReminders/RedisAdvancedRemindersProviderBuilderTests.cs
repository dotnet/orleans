using System.IO;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.AdvancedReminders.Redis;
using Orleans.Hosting;
using Xunit;

namespace Tester.Redis.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
public class RedisAdvancedRemindersProviderBuilderTests
{
    [Theory]
    [InlineData("Never")]
    [InlineData("AfterInactivity(01:00:00)")]
    public void Configure_BindsTableWideExpiry(string configured)
    {
        var builder = new TestSiloBuilder(new Dictionary<string, string?>
        {
            ["Settings:TableExpiry"] = configured,
            ["Settings:ConnectionString"] = "localhost:6379,abortConnect=false",
        });
        new AdvancedRedisRemindersProviderBuilder().Configure(builder, "Redis", builder.Configuration.GetSection("Settings"));
        using var provider = builder.Services.BuildServiceProvider();

        Assert.Equal(configured, provider.GetRequiredService<IOptions<RedisReminderTableOptions>>().Value.TableExpiry.ToString());
    }

    [Theory]
    [InlineData("EntryExpiry", "01:00:00")]
    [InlineData("EntryExpiry", null)]
    [InlineData("TableExpiry", null)]
    [InlineData("TableExpiry", "")]
    [InlineData("TableExpiry", " ")]
    [InlineData("TableExpiry", "0")]
    [InlineData("TableExpiry", "AfterInactivity(00:00:00)")]
    public void Configure_RejectsAmbiguousOrInvalidExpiry(string key, string? value)
    {
        var builder = new TestSiloBuilder(new Dictionary<string, string?> { [$"Settings:{key}"] = value });
        new AdvancedRedisRemindersProviderBuilder().Configure(builder, "Redis", builder.Configuration.GetSection("Settings"));
        using var provider = builder.Services.BuildServiceProvider();

        if (key == "EntryExpiry" || string.IsNullOrWhiteSpace(value))
        {
            Assert.Throws<OrleansConfigurationException>(() => provider.GetRequiredService<IOptions<RedisReminderTableOptions>>().Value);
        }
        else
        {
            Assert.Throws<FormatException>(() => provider.GetRequiredService<IOptions<RedisReminderTableOptions>>().Value);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Configure_RejectsMissingJsonExpiry(string? configured)
    {
        var value = System.Text.Json.JsonSerializer.Serialize(configured);
        var builder = new TestSiloBuilder($$"""
        { "Settings": { "TableExpiry": {{value}} } }
        """);
        new AdvancedRedisRemindersProviderBuilder().Configure(builder, "Redis", builder.Configuration.GetSection("Settings"));
        using var provider = builder.Services.BuildServiceProvider();

        Assert.Throws<OrleansConfigurationException>(() => provider.GetRequiredService<IOptions<RedisReminderTableOptions>>().Value);
    }

    private sealed class TestSiloBuilder(Dictionary<string, string?> settings) : ISiloBuilder
    {
        public TestSiloBuilder(string json) : this([])
        {
            Configuration = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();
        }

        public IServiceCollection Services { get; } = new ServiceCollection();
        public IConfiguration Configuration { get; } = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }
}
