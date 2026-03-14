extern alias AdvancedRemindersAzureStorage;

#nullable enable
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tester;
using Tester.AzureUtils;
using TestExtensions;
using Xunit;
using AzureBasedReminderTable = AdvancedRemindersAzureStorage::Orleans.AdvancedReminders.Runtime.ReminderService.AzureBasedReminderTable;
using AzureTableReminderStorageOptions = AdvancedRemindersAzureStorage::Orleans.AdvancedReminders.AzureStorage.AzureTableReminderStorageOptions;

namespace UnitTests.AdvancedRemindersTest;

[TestCategory("Reminders"), TestCategory("AzureStorage")]
public class AzureAdvancedRemindersTableTests : AdvancedReminderTableTestsBase
{
    public AzureAdvancedRemindersTableTests(ConnectionStringFixture fixture, TestEnvironmentFixture environment)
        : base(fixture, environment, CreateFilters())
    {
        TestUtils.CheckForAzureStorage();
    }

    private static LoggerFilterOptions CreateFilters()
    {
        var filters = new LoggerFilterOptions();
        filters.AddFilter("AzureTableDataManager", LogLevel.Trace);
        filters.AddFilter("OrleansSiloInstanceManager", LogLevel.Trace);
        filters.AddFilter("Storage", LogLevel.Trace);
        return filters;
    }

    protected override Orleans.AdvancedReminders.IReminderTable CreateRemindersTable()
    {
        TestUtils.CheckForAzureStorage();
        var options = Options.Create(new AzureTableReminderStorageOptions());
        options.Value.TableServiceClient = AzureStorageOperationOptionsExtensions.GetTableServiceClient();
        return new AzureBasedReminderTable(loggerFactory, clusterOptions, options);
    }

    protected override Task<string> GetConnectionString()
    {
        TestUtils.CheckForAzureStorage();
        return Task.FromResult("not used");
    }

    [SkippableFact]
    public async Task RemindersTable_Azure_DurableCronRoundTrip() => await ReminderCronRoundTrip();

    [SkippableFact]
    public async Task RemindersTable_Azure_DurableAdaptiveFieldsRoundTrip() => await ReminderAdaptiveFieldsRoundTrip();

    [SkippableFact]
    public async Task RemindersTable_Azure_DurableCronTimeZoneRoundTrip() => await ReminderCronTimeZoneRoundTrip();
}
