extern alias DurableRemindersRedis;

#nullable enable
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TestExtensions;
using UnitTests;
using UnitTests.DurableRemindersTest;
using Xunit;
using RedisReminderTable = DurableRemindersRedis::Orleans.DurableReminders.Redis.RedisReminderTable;
using RedisReminderTableOptions = DurableRemindersRedis::Orleans.Configuration.RedisReminderTableOptions;
using ClusterOptions = Orleans.Configuration.ClusterOptions;
using LoggerFilterOptions = Microsoft.Extensions.Logging.LoggerFilterOptions;

namespace Tester.Redis.DurableReminders;

[TestCategory("Redis"), TestCategory("Reminders"), TestCategory("Functional")]
[Collection(TestEnvironmentFixture.DefaultCollection)]
public class RedisDurableRemindersTableTests : DurableReminderTableTestsBase
{
    public RedisDurableRemindersTableTests(ConnectionStringFixture fixture, CommonFixture clusterFixture)
        : base(fixture, clusterFixture, CreateFilters())
    {
        TestUtils.CheckForRedis();
    }

    private static LoggerFilterOptions CreateFilters()
    {
        var filters = new LoggerFilterOptions();
        filters.AddFilter(nameof(RedisDurableRemindersTableTests), LogLevel.Trace);
        return filters;
    }

    protected override Orleans.DurableReminders.IReminderTable CreateRemindersTable()
    {
        TestUtils.CheckForRedis();
        return new RedisReminderTable(
            loggerFactory.CreateLogger<RedisReminderTable>(),
            clusterOptions,
            Options.Create(new RedisReminderTableOptions
            {
                ConfigurationOptions = ConfigurationOptions.Parse(GetConnectionString().Result),
                EntryExpiry = TimeSpan.FromHours(1),
            }));
    }

    protected override Task<string> GetConnectionString() => Task.FromResult(TestDefaultConfiguration.RedisConnectionString);

    [SkippableFact]
    public async Task RemindersTable_Redis_DurableCronRoundTrip() => await ReminderCronRoundTrip();

    [SkippableFact]
    public async Task RemindersTable_Redis_DurableAdaptiveFieldsRoundTrip() => await ReminderAdaptiveFieldsRoundTrip();

    [SkippableFact]
    public async Task RemindersTable_Redis_DurableCronTimeZoneRoundTrip() => await ReminderCronTimeZoneRoundTrip();
}
