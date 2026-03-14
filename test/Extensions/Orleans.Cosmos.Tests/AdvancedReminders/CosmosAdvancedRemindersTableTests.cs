#nullable enable
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.AdvancedReminders.Cosmos;
using TestExtensions;
using Tester.Cosmos;
using UnitTests.AdvancedRemindersTest;
using Xunit;

namespace UnitTests.AdvancedRemindersTest;

[TestCategory("Reminders"), TestCategory("Cosmos")]
public class CosmosAdvancedRemindersTableTests : AdvancedReminderTableTestsBase
{
    public CosmosAdvancedRemindersTableTests(ConnectionStringFixture fixture, TestEnvironmentFixture environment)
        : base(fixture, environment, CreateFilters())
    {
        CosmosTestUtils.CheckCosmosStorage();
    }

    private static LoggerFilterOptions CreateFilters()
    {
        var filters = new LoggerFilterOptions();
        filters.AddFilter(nameof(CosmosAdvancedRemindersTableTests), LogLevel.Trace);
        filters.AddFilter("CosmosReminderTable", LogLevel.Trace);
        return filters;
    }

    protected override Orleans.AdvancedReminders.IReminderTable CreateRemindersTable()
    {
        CosmosTestUtils.CheckCosmosStorage();
        var options = Options.Create(new CosmosReminderTableOptions());
        ConfigureTestDefaults(options.Value);
        return new CosmosReminderTable(loggerFactory, ClusterFixture.Services, options, clusterOptions);
    }

    protected override Task<string> GetConnectionString()
    {
        CosmosTestUtils.CheckCosmosStorage();
        return Task.FromResult(TestDefaultConfiguration.CosmosDBAccountEndpoint);
    }

    [SkippableFact]
    public async Task RemindersTable_Cosmos_DurableCronRoundTrip() => await ReminderCronRoundTrip();

    [SkippableFact]
    public async Task RemindersTable_Cosmos_DurableAdaptiveFieldsRoundTrip() => await ReminderAdaptiveFieldsRoundTrip();

    [SkippableFact]
    public async Task RemindersTable_Cosmos_DurableCronTimeZoneRoundTrip() => await ReminderCronTimeZoneRoundTrip();

    private static void ConfigureTestDefaults(CosmosReminderTableOptions options)
    {
        if (TestDefaultConfiguration.UseAadAuthentication)
        {
            options.ConfigureCosmosClient(TestDefaultConfiguration.CosmosDBAccountEndpoint, TestDefaultConfiguration.TokenCredential);
        }
        else
        {
            options.ConfigureCosmosClient(_ => new ValueTask<CosmosClient>(CreateCosmosClientUsingAccountKey()));
        }

        options.IsResourceCreationEnabled = true;
    }

    private static CosmosClient CreateCosmosClientUsingAccountKey()
    {
        var cosmosClientOptions = new CosmosClientOptions
        {
            HttpClientFactory = static () =>
            {
                HttpMessageHandler httpMessageHandler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
                };

                return new HttpClient(httpMessageHandler);
            },
            ConnectionMode = ConnectionMode.Gateway,
        };

        return new CosmosClient(
            TestDefaultConfiguration.CosmosDBAccountEndpoint,
            TestDefaultConfiguration.CosmosDBAccountKey,
            cosmosClientOptions);
    }
}
