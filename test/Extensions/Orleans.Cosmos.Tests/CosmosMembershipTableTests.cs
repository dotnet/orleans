using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TestExtensions;
using UnitTests.MembershipTests;
using Orleans.Messaging;
using Orleans.Clustering.Cosmos;
using UnitTests;

namespace Tester.Cosmos.Clustering;

/// <summary>
/// Tests for operation of Orleans Membership Table using Azure Cosmos DB - Requires access to external Azure Cosmos DB account
/// 
/// Azure Cosmos DB provides a globally distributed, multi-model database service that Orleans can use for cluster membership
/// management.
/// 
/// These tests verify the Cosmos DB membership provider correctly implements
/// all membership operations with Cosmos DB's unique features like:
/// - Document-based storage with SQL querying
/// - Optimistic concurrency using ETags
/// - Partition key strategies for cluster isolation
/// </summary>
[TestCategory("Membership"), TestCategory("Cosmos")]
public class CosmosMembershipTableTests : MembershipTableTestsBase
{
    public CosmosMembershipTableTests(ConnectionStringFixture fixture, TestEnvironmentFixture environment) : base(fixture, environment, CreateFilters())
    {
    }

    private static LoggerFilterOptions CreateFilters()
    {
        var filters = new LoggerFilterOptions();
        filters.AddFilter(typeof(CosmosMembershipTable).FullName, LogLevel.Trace);
        filters.AddFilter("Orleans.Storage", LogLevel.Trace);
        return filters;
    }

    /// <summary>
    /// Creates a Cosmos DB-based membership table for testing.
    /// Configures the Cosmos DB client with test-specific settings
    /// including database/container names and consistency levels.
    /// </summary>
    protected override IMembershipTable CreateMembershipTable(ILogger logger)
    {
        CosmosTestUtils.CheckCosmosStorage();
        var options = new CosmosClusteringOptions();
        options.ConfigureTestDefaults();
        return new CosmosMembershipTable(loggerFactory, Services, Options.Create(options), _clusterOptions);
    }

    /// <summary>
    /// Creates a Cosmos DB-based gateway list provider.
    /// Uses Cosmos DB's querying capabilities to efficiently
    /// retrieve available gateway silos for client connections.
    /// </summary>
    protected override IGatewayListProvider CreateGatewayListProvider(ILogger logger)
    {
        var options = new CosmosClusteringOptions();
        options.ConfigureTestDefaults();
        return new CosmosGatewayListProvider(loggerFactory, Services, Options.Create(options), _clusterOptions, _gatewayOptions);
    }

    protected override Task<string> GetConnectionString()
    {
        return Task.FromResult(TestDefaultConfiguration.CosmosDBAccountKey);
    }

    [SkippableFact, TestCategory("Functional")]
    public void MembershipTable_Cosmos_Init()
    {
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task MembershipTable_Cosmos_GetGateways()
    {
        await MembershipTable_GetGateways();
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task MembershipTable_Cosmos_ReadAll_EmptyTable()
    {
        await MembershipTable_ReadAll_EmptyTable();
    }

    /// <summary>
    /// Tests inserting a silo entry as a Cosmos DB document.
    /// Verifies document creation with proper partition key assignment
    /// and automatic indexing for efficient queries.
    /// </summary>
    [SkippableFact, TestCategory("Functional")]
    public async Task MembershipTable_Cosmos_InsertRow()
    {
        await MembershipTable_InsertRow();
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task MembershipTable_Cosmos_ReadRow_Insert_Read()
    {
        await MembershipTable_ReadRow_Insert_Read();
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task MembershipTable_Cosmos_ReadAll_Insert_ReadAll()
    {
        await MembershipTable_ReadAll_Insert_ReadAll();
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task MembershipTable_Cosmos_UpdateRow()
    {
        await MembershipTable_UpdateRow();
    }

    /// <summary>
    /// Tests concurrent updates using Cosmos DB's ETag-based concurrency.
    /// Verifies that optimistic concurrency control prevents
    /// conflicting updates and ensures data consistency.
    /// </summary>
    [SkippableFact, TestCategory("Functional")]
    public async Task MembershipTable_Cosmos_UpdateRowInParallel()
    {
        await MembershipTable_UpdateRowInParallel();
    }

    /// <summary>
    /// Tests heartbeat updates in Cosmos DB.
    /// Verifies efficient partial document updates for liveness
    /// information without rewriting entire membership entries.
    /// </summary>
    [SkippableFact, TestCategory("Functional")]
    public async Task MembershipTable_Cosmos_UpdateIAmAlive()
    {
        await MembershipTable_UpdateIAmAlive();
    }
}
