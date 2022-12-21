using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TestExtensions;
using UnitTests.MembershipTests;
using Orleans.Messaging;
using Orleans.Clustering.AzureCosmos;
using UnitTests;

namespace Tester.AzureCosmos.Clustering;

/// <summary>
/// Tests for operation of Orleans Membership Table using Azure Cosmos DB - Requires access to external Azure Cosmos DB account
/// </summary>
[TestCategory("Membership"), TestCategory("AzureCosmosDB")]
public class AzureCosmosMembershipTableTests : MembershipTableTestsBase
{
    public AzureCosmosMembershipTableTests(ConnectionStringFixture fixture, TestEnvironmentFixture environment) : base(fixture, environment, CreateFilters())
    {
    }

    private static LoggerFilterOptions CreateFilters()
    {
        var filters = new LoggerFilterOptions();
        filters.AddFilter(typeof(AzureCosmosMembershipTable).FullName, LogLevel.Trace);
        filters.AddFilter("Orleans.Storage", LogLevel.Trace);
        return filters;
    }

    protected override IMembershipTable CreateMembershipTable(ILogger logger)
    {
        AzureCosmosTestUtils.CheckCosmosDbStorage();
        var options = new AzureCosmosClusteringOptions();
        options.ConfigureTestDefaults();
        return new AzureCosmosMembershipTable(loggerFactory, Services, Options.Create(options), _clusterOptions);
    }

    protected override IGatewayListProvider CreateGatewayListProvider(ILogger logger)
    {
        var options = new AzureCosmosClusteringOptions();
        options.ConfigureTestDefaults();
        return new AzureCosmosGatewayListProvider(loggerFactory, Services, Options.Create(options), _clusterOptions, _gatewayOptions);
    }

    protected override Task<string> GetConnectionString()
    {
        return Task.FromResult(TestDefaultConfiguration.CosmosDBAccountKey);
    }

    [SkippableFact, TestCategory("Functional")]
    public void MembershipTable_AzureCosmos_Init()
    {
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task MembershipTable_AzureCosmos_GetGateways()
    {
        await MembershipTable_GetGateways();
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task MembershipTable_AzureCosmos_ReadAll_EmptyTable()
    {
        await MembershipTable_ReadAll_EmptyTable();
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task MembershipTable_AzureCosmos_InsertRow()
    {
        await MembershipTable_InsertRow();
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task MembershipTable_AzureCosmos_ReadRow_Insert_Read()
    {
        await MembershipTable_ReadRow_Insert_Read();
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task MembershipTable_AzureCosmos_ReadAll_Insert_ReadAll()
    {
        await MembershipTable_ReadAll_Insert_ReadAll();
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task MembershipTable_AzureCosmos_UpdateRow()
    {
        await MembershipTable_UpdateRow();
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task MembershipTable_AzureCosmos_UpdateRowInParallel()
    {
        await MembershipTable_UpdateRowInParallel();
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task MembershipTable_AzureCosmos_UpdateIAmAlive()
    {
        await MembershipTable_UpdateIAmAlive();
    }
}
