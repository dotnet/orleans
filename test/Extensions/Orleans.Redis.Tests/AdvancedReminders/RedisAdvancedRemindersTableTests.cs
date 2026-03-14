extern alias AdvancedRemindersRedis;

#nullable enable
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TestExtensions;
using UnitTests;
using UnitTests.AdvancedRemindersTest;
using Xunit;
using RedisReminderTable = AdvancedRemindersRedis::Orleans.AdvancedReminders.Redis.RedisReminderTable;
using RedisReminderTableOptions = AdvancedRemindersRedis::Orleans.Configuration.RedisReminderTableOptions;
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
