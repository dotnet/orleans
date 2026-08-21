#nullable enable
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.AdvancedReminders.Redis;
using Orleans.Runtime;
using StackExchange.Redis;
using TestExtensions;
using UnitTests;
using UnitTests.AdvancedRemindersTest;
using Xunit;
using ClusterOptions = Orleans.Configuration.ClusterOptions;
using LoggerFilterOptions = Microsoft.Extensions.Logging.LoggerFilterOptions;

namespace Tester.Redis.AdvancedReminders;

[TestCategory("Redis"), TestCategory("Reminders"), TestCategory("Functional")]
[Collection(TestEnvironmentFixture.DefaultCollection)]
public class RedisAdvancedRemindersTableTests : AdvancedReminderTableTestsBase
{
    public RedisAdvancedRemindersTableTests(ConnectionStringFixture fixture, CommonFixture clusterFixture)
        : base(fixture, clusterFixture, CreateFilters())
    {
        TestUtils.CheckForRedis();
    }

    private static LoggerFilterOptions CreateFilters()
    {
        var filters = new LoggerFilterOptions();
        filters.AddFilter(nameof(RedisAdvancedRemindersTableTests), LogLevel.Trace);
        return filters;
    }

    protected override Orleans.AdvancedReminders.IReminderTable CreateRemindersTable()
    {
        TestUtils.CheckForRedis();
        return new RedisReminderTable(
            loggerFactory.CreateLogger<RedisReminderTable>(),
            clusterOptions,
            Options.Create(new RedisReminderTableOptions
            {
                ConfigurationOptions = ConfigurationOptions.Parse(TestDefaultConfiguration.RedisConnectionString!),
                EntryExpiry = TimeSpan.FromHours(1),
            }));
    }

    protected override Task<string> GetConnectionString() => Task.FromResult(TestDefaultConfiguration.RedisConnectionString!);

    [Fact]
    public async Task RemindersTable_Redis_DurableCronRoundTrip() => await ReminderCronRoundTrip();

    [Fact]
    public async Task RemindersTable_Redis_DurableAdaptiveFieldsRoundTrip() => await ReminderAdaptiveFieldsRoundTrip();

    [Fact]
    public async Task RemindersTable_Redis_DurableCronTimeZoneRoundTrip() => await ReminderCronTimeZoneRoundTrip();

    [Fact]
    public async Task RemindersTable_Redis_UpsertAppliesEntryExpiryWhenKeyIsCreated()
    {
        TestUtils.CheckForRedis();
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(TestDefaultConfiguration.RedisConnectionString!);
        var database = multiplexer.GetDatabase();
        var key = (RedisKey)$"{clusterOptions.Value.ServiceId}/advanced-reminders";
        await database.KeyDeleteAsync(key);
        var expiry = TimeSpan.FromMinutes(10);
        await using var table = new RedisReminderTable(
            loggerFactory.CreateLogger<RedisReminderTable>(),
            clusterOptions,
            Options.Create(new RedisReminderTableOptions
            {
                ConfigurationOptions = ConfigurationOptions.Parse(TestDefaultConfiguration.RedisConnectionString!),
                EntryExpiry = expiry,
                CreateMultiplexer = _ => Task.FromResult<(IConnectionMultiplexer Multiplexer, bool IsShared)>((multiplexer, true)),
            }));

        try
        {
            await table.Init();
            await table.UpsertRow(new Orleans.AdvancedReminders.ReminderEntry
            {
                GrainId = GrainId.Create("redis-ttl", Guid.NewGuid().ToString("N")),
                ReminderName = "ttl",
                StartAt = DateTime.UtcNow.AddMinutes(1),
                NextDueUtc = DateTime.UtcNow.AddMinutes(1),
                Period = TimeSpan.FromMinutes(1),
            });

            var timeToLive = await database.KeyTimeToLiveAsync(key);
            Assert.NotNull(timeToLive);
            Assert.InRange(timeToLive.Value, expiry - TimeSpan.FromMinutes(1), expiry);
        }
        finally
        {
            await database.KeyDeleteAsync(key);
        }
    }

}

[TestCategory("Redis"), TestCategory("Reminders")]
public class RedisAdvancedReminderOptionsTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void OptionsValidator_InvalidDelegateOrExpiry_Throws(bool nullDelegate, bool invalidExpiry)
    {
        var options = new RedisReminderTableOptions
        {
            ConfigurationOptions = new ConfigurationOptions(),
            CreateMultiplexer = nullDelegate ? null! : RedisReminderTableOptions.DefaultCreateMultiplexer,
            EntryExpiry = invalidExpiry ? TimeSpan.Zero : null,
        };
        var validator = new RedisReminderTableOptionsValidator(Options.Create(options));

        Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);
    }
}
