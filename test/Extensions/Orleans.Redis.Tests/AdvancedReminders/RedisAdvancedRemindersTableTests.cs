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

[TestSuite("Functional")]
[TestProvider("Redis")]
[TestArea("Reminders")]
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
                TableExpiry = ReminderTableExpiry.AfterInactivity(TimeSpan.FromHours(1)),
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
    public async Task RemindersTable_Redis_AfterInactivityExpiresOnlyAfterSuccessfulUpserts()
    {
        TestUtils.CheckForRedis();
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(TestDefaultConfiguration.RedisConnectionString!);
        var database = multiplexer.GetDatabase();
        var isolatedClusterOptions = Options.Create(new ClusterOptions { ServiceId = Guid.NewGuid().ToString("N") });
        var key = (RedisKey)$"{isolatedClusterOptions.Value.ServiceId}/advanced-reminders";
        var expiry = TimeSpan.FromMinutes(10);
        await using var table = new RedisReminderTable(
            loggerFactory.CreateLogger<RedisReminderTable>(),
            isolatedClusterOptions,
            Options.Create(new RedisReminderTableOptions
            {
                ConfigurationOptions = ConfigurationOptions.Parse(TestDefaultConfiguration.RedisConnectionString!),
                TableExpiry = ReminderTableExpiry.AfterInactivity(expiry),
                CreateMultiplexer = _ => Task.FromResult<(IConnectionMultiplexer Multiplexer, bool IsShared)>((multiplexer, true)),
            }));

        try
        {
            await table.StartAsync(CancellationToken.None);
            var entry = new Orleans.AdvancedReminders.ReminderEntry
            {
                GrainId = GrainId.Create("redis-ttl", Guid.NewGuid().ToString("N")),
                ReminderName = "ttl",
                StartAt = DateTime.UtcNow.AddMinutes(1),
                NextDueUtc = DateTime.UtcNow.AddMinutes(1),
                Period = TimeSpan.FromMinutes(1),
            };
            var etag = await table.UpsertRow(entry);

            var timeToLive = await database.KeyTimeToLiveAsync(key);
            Assert.NotNull(timeToLive);
            Assert.InRange(timeToLive.Value, expiry - TimeSpan.FromMinutes(1), expiry);

            await database.KeyExpireAsync(key, TimeSpan.FromMinutes(1));
            entry.ETag = "stale-etag";
            await Assert.ThrowsAsync<Orleans.AdvancedReminders.Runtime.ReminderException>(() => table.UpsertRow(entry));
            var unchangedTtl = await database.KeyTimeToLiveAsync(key);
            Assert.NotNull(unchangedTtl);
            Assert.InRange(unchangedTtl.Value, TimeSpan.Zero, TimeSpan.FromMinutes(1));

            entry.ETag = etag;
            await table.UpsertRow(entry);
            var refreshedTtl = await database.KeyTimeToLiveAsync(key);
            Assert.NotNull(refreshedTtl);
            Assert.InRange(refreshedTtl.Value, expiry - TimeSpan.FromMinutes(1), expiry);
        }
        finally
        {
            await database.KeyDeleteAsync(key);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RemindersTable_Redis_NeverExpiryPersistsExistingKey(bool onStartup)
    {
        TestUtils.CheckForRedis();
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(TestDefaultConfiguration.RedisConnectionString!);
        var database = multiplexer.GetDatabase();
        var isolatedClusterOptions = Options.Create(new ClusterOptions { ServiceId = Guid.NewGuid().ToString("N") });
        var key = (RedisKey)$"{isolatedClusterOptions.Value.ServiceId}/advanced-reminders";
        await database.SortedSetAddAsync(key, "existing", 0);
        await database.KeyExpireAsync(key, TimeSpan.FromMinutes(10));
        await using var table = new RedisReminderTable(
            loggerFactory.CreateLogger<RedisReminderTable>(),
            isolatedClusterOptions,
            Options.Create(new RedisReminderTableOptions
            {
                ConfigurationOptions = ConfigurationOptions.Parse(TestDefaultConfiguration.RedisConnectionString!),
                TableExpiry = ReminderTableExpiry.Never,
                CreateMultiplexer = _ => Task.FromResult<(IConnectionMultiplexer Multiplexer, bool IsShared)>((multiplexer, true)),
            }));

        try
        {
            await table.StartAsync(CancellationToken.None);
            if (!onStartup)
            {
                await database.KeyExpireAsync(key, TimeSpan.FromMinutes(10));
                await table.UpsertRow(new Orleans.AdvancedReminders.ReminderEntry
                {
                    GrainId = GrainId.Create("redis-persist", Guid.NewGuid().ToString("N")),
                    ReminderName = "persist",
                    StartAt = DateTime.UtcNow.AddMinutes(1),
                    NextDueUtc = DateTime.UtcNow.AddMinutes(1),
                    Period = TimeSpan.FromMinutes(1),
                });
            }

            Assert.Null(await database.KeyTimeToLiveAsync(key));
        }
        finally
        {
            await database.KeyDeleteAsync(key);
        }
    }

}

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Redis"), TestCategory("Reminders")]
public class RedisAdvancedReminderOptionsTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void OptionsValidator_MissingDelegateOrExpiry_Throws(bool nullDelegate, bool missingExpiry)
    {
        var options = new RedisReminderTableOptions
        {
            ConfigurationOptions = new ConfigurationOptions(),
            CreateMultiplexer = nullDelegate ? null! : RedisReminderTableOptions.DefaultCreateMultiplexer,
            TableExpiry = missingExpiry ? null! : ReminderTableExpiry.Never,
        };
        var validator = new RedisReminderTableOptionsValidator(Options.Create(options));

        Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);
    }
}
