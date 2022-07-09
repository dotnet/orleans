using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TestExtensions;
using UnitTests.MembershipTests;
using Xunit;
using Orleans;
using Orleans.Messaging;
using Orleans.Clustering.CosmosDB;
using UnitTests;
using Tester.CosmosDB;

namespace Tester.AzureUtils;

/// <summary>
/// Tests for operation of Orleans Membership Table using Azure CosmosDB - Requires access to external Azure CosmosDB account
/// </summary>
[TestCategory("Membership"), TestCategory("AzureCosmosDB")]
public class AzureCosmosDBMembershipTableTests : MembershipTableTestsBase
{
    public AzureCosmosDBMembershipTableTests(ConnectionStringFixture fixture, TestEnvironmentFixture environment) : base(fixture, environment, CreateFilters())
    {
    }

    private static LoggerFilterOptions CreateFilters()
    {
        var filters = new LoggerFilterOptions();
        filters.AddFilter(typeof(AzureCosmosDBMembershipTable).FullName, LogLevel.Trace);
        filters.AddFilter("Orleans.Storage", LogLevel.Trace);
        return filters;
    }

    protected override IMembershipTable CreateMembershipTable(ILogger logger)
    {
        CosmosDBTestUtils.CheckCosmosDbStorage();
        var options = new AzureCosmosDBClusteringOptions();
        options.ConfigureTestDefaults();
        return new AzureCosmosDBMembershipTable(loggerFactory, this.Services, Options.Create(options), this.clusterOptions);
    }

    protected override IGatewayListProvider CreateGatewayListProvider(ILogger logger)
    {
        var options = new AzureCosmosDBClusteringOptions();
        options.ConfigureTestDefaults();
        return new AzureCosmosDBGatewayListProvider(loggerFactory, this.Services, Options.Create(options), this.clusterOptions, this.gatewayOptions);
    }

    protected override Task<string> GetConnectionString()
    {
        return Task.FromResult(TestDefaultConfiguration.DataConnectionString);
    }

    [SkippableFact, TestCategory("Functional")]
    public void MembershipTable_Azure_Init()
    {
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task MembershipTable_Azure_GetGateways()
    {
        await MembershipTable_GetGateways();
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task MembershipTable_Azure_ReadAll_EmptyTable()
    {
        await MembershipTable_ReadAll_EmptyTable();
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task MembershipTable_Azure_InsertRow()
    {
        await MembershipTable_InsertRow();
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task MembershipTable_Azure_ReadRow_Insert_Read()
    {
        await MembershipTable_ReadRow_Insert_Read();
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task MembershipTable_Azure_ReadAll_Insert_ReadAll()
    {
        await MembershipTable_ReadAll_Insert_ReadAll();
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task MembershipTable_Azure_UpdateRow()
    {
        await MembershipTable_UpdateRow();
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task MembershipTable_Azure_UpdateRowInParallel()
    {
        await MembershipTable_UpdateRowInParallel();
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task MembershipTable_Azure_UpdateIAmAlive()
    {
        await MembershipTable_UpdateIAmAlive();
    }
}