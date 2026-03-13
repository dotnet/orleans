extern alias DurableRemindersAdoNet;

#nullable enable
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Tests.SqlUtils;
using TestExtensions;
using UnitTests.General;
using Xunit;
using ClusterOptions = Orleans.Configuration.ClusterOptions;
using LoggerFilterOptions = Microsoft.Extensions.Logging.LoggerFilterOptions;
using AdoNetReminderTable = DurableRemindersAdoNet::Orleans.DurableReminders.Runtime.ReminderService.AdoNetReminderTable;
using AdoNetReminderTableOptions = DurableRemindersAdoNet::Orleans.Configuration.AdoNetReminderTableOptions;

namespace UnitTests.DurableRemindersTest;

[TestCategory("Functional"), TestCategory("Reminders"), TestCategory("AdoNet"), TestCategory("PostgreSql")]
public class PostgreSqlDurableRemindersTableTests : DurableReminderTableTestsBase
{
    public PostgreSqlDurableRemindersTableTests(ConnectionStringFixture fixture, TestEnvironmentFixture environment)
        : base(fixture, environment, CreateFilters())
    {
    }

    private static LoggerFilterOptions CreateFilters()
    {
        var filters = new LoggerFilterOptions();
        filters.AddFilter(nameof(PostgreSqlDurableRemindersTableTests), LogLevel.Trace);
        return filters;
    }

    protected override Orleans.DurableReminders.IReminderTable CreateRemindersTable()
    {
        var options = new AdoNetReminderTableOptions
        {
            Invariant = GetAdoInvariant(),
            ConnectionString = connectionStringFixture.ConnectionString,
        };

        return new AdoNetReminderTable(clusterOptions, Options.Create(options));
    }

    protected override string GetAdoInvariant() => AdoNetInvariants.InvariantNamePostgreSql;

    protected override async Task<string> GetConnectionString()
    {
        var instance = await RelationalStorageForTesting.SetupInstance(GetAdoInvariant()!, testDatabaseName);
        return instance.CurrentConnectionString;
    }

    [SkippableFact]
    public async Task RemindersTable_PostgreSql_DurableCronRoundTrip() => await ReminderCronRoundTrip();

    [SkippableFact]
    public async Task RemindersTable_PostgreSql_DurableAdaptiveFieldsRoundTrip() => await ReminderAdaptiveFieldsRoundTrip();

    [SkippableFact]
    public async Task RemindersTable_PostgreSql_DurableCronTimeZoneRoundTrip() => await ReminderCronTimeZoneRoundTrip();
}
