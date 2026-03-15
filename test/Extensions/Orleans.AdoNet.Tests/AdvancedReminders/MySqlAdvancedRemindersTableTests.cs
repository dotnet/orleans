#nullable enable
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.AdvancedReminders.AdoNet;
using Orleans.AdvancedReminders.Runtime.ReminderService;
using Orleans.Tests.SqlUtils;
using TestExtensions;
using UnitTests.General;
using Xunit;
using ClusterOptions = Orleans.Configuration.ClusterOptions;
using LoggerFilterOptions = Microsoft.Extensions.Logging.LoggerFilterOptions;

namespace UnitTests.AdvancedRemindersTest;

[TestCategory("Functional"), TestCategory("Reminders"), TestCategory("AdoNet"), TestCategory("MySql")]
public class MySqlAdvancedRemindersTableTests : AdvancedReminderTableTestsBase
{
    public MySqlAdvancedRemindersTableTests(ConnectionStringFixture fixture, TestEnvironmentFixture environment)
        : base(fixture, environment, CreateFilters())
    {
    }

    private static LoggerFilterOptions CreateFilters()
    {
        var filters = new LoggerFilterOptions();
        filters.AddFilter(nameof(MySqlAdvancedRemindersTableTests), LogLevel.Trace);
        return filters;
    }

    protected override Orleans.AdvancedReminders.IReminderTable CreateRemindersTable()
    {
        var options = new AdoNetReminderTableOptions
        {
            Invariant = GetAdoInvariant(),
            ConnectionString = connectionStringFixture.ConnectionString,
        };

        return new AdoNetReminderTable(clusterOptions, Options.Create(options));
    }

    protected override string GetAdoInvariant() => AdoNetInvariants.InvariantNameMySql;

    protected override async Task<string> GetConnectionString()
    {
        var instance = await RelationalStorageForTesting.SetupInstance(GetAdoInvariant()!, testDatabaseName);
        return instance.CurrentConnectionString;
    }

    [SkippableFact]
    public async Task RemindersTable_MySql_DurableCronRoundTrip() => await ReminderCronRoundTrip();

    [SkippableFact]
    public async Task RemindersTable_MySql_DurableAdaptiveFieldsRoundTrip() => await ReminderAdaptiveFieldsRoundTrip();

    [SkippableFact]
    public async Task RemindersTable_MySql_DurableCronTimeZoneRoundTrip() => await ReminderCronTimeZoneRoundTrip();
}
