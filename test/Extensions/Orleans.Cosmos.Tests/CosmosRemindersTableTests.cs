using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Reminders.Cosmos;
using TestExtensions;
using UnitTests;
using UnitTests.RemindersTest;
using Xunit;

namespace Tester.Cosmos.Reminders;

/// <summary>
/// Tests for operation of the Orleans reminders table using Azure Cosmos DB.
/// </summary>
[TestCategory("Reminders"), TestCategory("Cosmos")]
public class CosmosRemindersTableTests : ReminderTableTestsBase
{
    public CosmosRemindersTableTests(ConnectionStringFixture fixture, TestEnvironmentFixture environment)
        : base(fixture, environment, CreateFilters())
    {
        CosmosTestUtils.CheckCosmosStorage();
    }

    private static LoggerFilterOptions CreateFilters()
    {
        var filters = new LoggerFilterOptions();
        filters.AddFilter(typeof(CosmosReminderTable).FullName, LogLevel.Trace);
        return filters;
    }

    protected override IReminderTable CreateRemindersTable()
    {
        CosmosTestUtils.CheckCosmosStorage();

        var options = new CosmosReminderTableOptions();
        options.ConfigureTestDefaults();
        return new CosmosReminderTable(loggerFactory, this.ClusterFixture.Services, Options.Create(options), this.clusterOptions);
    }

    protected override Task<string> GetConnectionString()
    {
        return Task.FromResult(TestDefaultConfiguration.CosmosDBAccountKey);
    }

    [SkippableFact, TestCategory("Functional")]
    public void RemindersTable_Cosmos_Init()
    {
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task RemindersTable_Cosmos_RemindersRange()
    {
        await RemindersRange(50);
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task RemindersTable_Cosmos_RemindersParallelUpsert()
    {
        await RemindersParallelUpsert();
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task RemindersTable_Cosmos_ReminderSimple()
    {
        await ReminderSimple();
    }
}
