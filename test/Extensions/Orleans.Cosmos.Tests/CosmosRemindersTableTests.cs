using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Reminders.Cosmos;
using Orleans.Reminders.TestKit;
using TestExtensions;
using UnitTests;
using UnitTests.RemindersTest;
using Xunit;

namespace Tester.Cosmos.Reminders;

/// <summary>
/// Tests for operation of the Orleans reminders table using Azure Cosmos DB.
/// </summary>
[TestProvider("Cosmos")]
[TestArea("Reminders")]
[TestCategory("Reminders"), TestCategory("Cosmos")]
[TestSuite("Functional")]
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

    protected override ReminderTableCapabilities CreateReminderTableCapabilities()
        => ReminderTableProviderProfiles.Cosmos(GetType().Name);

    protected override Task<string> GetConnectionString()
    {
        return Task.FromResult(TestDefaultConfiguration.CosmosDBAccountKey!);
    }

    [TestSuite("Functional")]
    [Fact, TestCategory("Functional")]
    public void RemindersTable_Cosmos_Init()
    {
    }

    [TestSuite("Functional")]
    [Fact, TestCategory("Functional")]
    public async Task RemindersTable_Cosmos_RemindersRange()
    {
        await RemindersRange(50);
    }

    [TestSuite("Functional")]
    [Fact, TestCategory("Functional")]
    public async Task RemindersTable_Cosmos_RemindersParallelUpsert()
    {
        await RemindersParallelUpsert();
    }

    [TestSuite("Functional")]
    [Fact, TestCategory("Functional")]
    public async Task RemindersTable_Cosmos_ReminderSimple()
    {
        await ReminderSimple();
    }
}
