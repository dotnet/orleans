using System.Collections.Immutable;
using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TestExtensions;
using UnitTests.MembershipTests;
using Orleans.Messaging;
using Orleans.Clustering.Cosmos;
using Orleans.Clustering.Cosmos.Models;
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
[TestSuite("Functional")]
[TestProvider("Cosmos")]
[TestArea("Membership")]
public class CosmosMembershipTableTests : MembershipTableTestsBase
{
    private const string CosmosEmulatorTransactionalBatchConditionSkipReason = "The Cosmos DB emulator does not enforce the transactional batch ETag conditions required by this test.";

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
        return Task.FromResult(TestDefaultConfiguration.CosmosDBAccountKey!);
    }

    [Fact, TestCategory("Functional")]
    public void MembershipTable_Cosmos_Init()
    {
    }

    [Fact, TestCategory("Functional")]
    public async Task MembershipTable_Cosmos_GetGateways()
    {
        await MembershipTable_GetGateways();
    }

    [Fact, TestCategory("Functional")]
    public async Task MembershipTable_Cosmos_ReadAll_EmptyTable()
    {
        await MembershipTable_ReadAll_EmptyTable();
    }

    /// <summary>
    /// Tests inserting a silo entry as a Cosmos DB document.
    /// Verifies document creation with proper partition key assignment
    /// and automatic indexing for efficient queries.
    /// </summary>
    [Fact, TestCategory("Functional")]
    public async Task MembershipTable_Cosmos_InsertRow()
    {
        await MembershipTable_InsertRow();
    }

    [Fact, TestCategory("Functional")]
    public async Task MembershipTable_Cosmos_MetadataRoundTrips()
    {
        await MembershipTable_MetadataRoundTrips();
    }

    [Fact, TestCategory("Functional")]
    public async Task MembershipTable_Cosmos_ReadRow_Insert_Read()
    {
        CosmosTestUtils.SkipIfCosmosEmulator(CosmosEmulatorTransactionalBatchConditionSkipReason);

        await MembershipTable_ReadRow_Insert_Read();
    }

    [Fact, TestCategory("Functional")]
    public async Task MembershipTable_Cosmos_ReadAll_Insert_ReadAll()
    {
        await MembershipTable_ReadAll_Insert_ReadAll();
    }

    [Fact, TestCategory("Functional")]
    public async Task MembershipTable_Cosmos_UpdateRow()
    {
        CosmosTestUtils.SkipIfCosmosEmulator(CosmosEmulatorTransactionalBatchConditionSkipReason);

        await MembershipTable_UpdateRow();
    }

    /// <summary>
    /// Tests concurrent updates using Cosmos DB's ETag-based concurrency.
    /// Verifies that optimistic concurrency control prevents
    /// conflicting updates and ensures data consistency.
    /// </summary>
    [Fact, TestCategory("Functional")]
    public async Task MembershipTable_Cosmos_UpdateRowInParallel()
    {
        CosmosTestUtils.SkipIfCosmosEmulator(CosmosEmulatorTransactionalBatchConditionSkipReason);

        await MembershipTable_UpdateRowInParallel();
    }

    /// <summary>
    /// Tests heartbeat updates in Cosmos DB.
    /// Verifies efficient partial document updates for liveness
    /// information without rewriting entire membership entries.
    /// </summary>
    [Fact, TestCategory("Functional")]
    public async Task MembershipTable_Cosmos_UpdateIAmAlive()
    {
        await MembershipTable_UpdateIAmAlive();
    }

    [Fact, TestCategory("Functional")]
    public async Task MembershipMetadata_SurvivesLegacyReplacement_RejectsConflict_AndIsCleanedUp()
    {
        CosmosTestUtils.SkipIfCosmosEmulator(CosmosEmulatorTransactionalBatchConditionSkipReason);

        var options = new CosmosClusteringOptions();
        options.ConfigureTestDefaults();
        var membership = new CosmosMembershipTable(loggerFactory, Services, Options.Create(options), _clusterOptions);
        await membership.InitializeMembershipTable(false);

        var entry = CreateMetadataEntry();
        var initial = await membership.ReadAll();
        Assert.True(await membership.InsertRow(entry, initial.Version.Next()));

        using var client = await options.CreateClient(Services);
        var container = client.GetContainer(options.DatabaseName, options.ContainerName);
        var partitionKey = new PartitionKey(clusterId);
        var id = $"{entry.SiloAddress.Endpoint.Address}-{entry.SiloAddress.Endpoint.Port}-{entry.SiloAddress.Generation}";
        var legacyEntity = (await container.ReadItemAsync<SiloEntity>(id, partitionKey)).Resource;
        legacyEntity.Metadata = null;
        await container.ReplaceItemAsync(legacyEntity, id, partitionKey);

        var afterLegacyWrite = await membership.ReadRow(entry.SiloAddress);
        var storedTuple = Assert.Single(afterLegacyWrite.Members);
        Assert.Equal(entry.Metadata, storedTuple.Item1.Metadata);

        storedTuple.Item1.Metadata = ImmutableDictionary<string, string>.Empty.Add("region", "conflict");
        storedTuple.Item1.Status = SiloStatus.Dead;
        Assert.True(await membership.UpdateRow(storedTuple.Item1, storedTuple.Item2, afterLegacyWrite.Version.Next()));

        var afterConflict = await membership.ReadRow(entry.SiloAddress);
        Assert.Equal(entry.Metadata, Assert.Single(afterConflict.Members).Item1.Metadata);

        await membership.CleanupDefunctSiloEntries(DateTimeOffset.UtcNow.AddDays(1));
        Assert.Empty((await membership.ReadAll()).Members);

        var metadataContainer = client.GetContainer(
            options.DatabaseName,
            CosmosMembershipTable.GetMetadataContainerName(options));
        var exception = await Assert.ThrowsAsync<CosmosException>(
            () => metadataContainer.ReadItemAsync<SiloMetadataEntity>(id, partitionKey));
        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
    }

    [Fact, TestCategory("Functional")]
    public async Task MembershipMetadata_LegacyCleanupOrphanIsReconciled()
    {
        CosmosTestUtils.SkipIfCosmosEmulator(CosmosEmulatorTransactionalBatchConditionSkipReason);
        var options = new CosmosClusteringOptions();
        options.ConfigureTestDefaults();
        var membership = new CosmosMembershipTable(loggerFactory, Services, Options.Create(options), _clusterOptions);
        await membership.InitializeMembershipTable(false);
        var entry = CreateMetadataEntry();
        var initial = await membership.ReadAll();
        Assert.True(await membership.InsertRow(entry, initial.Version.Next()));

        using var client = await options.CreateClient(Services);
        var partitionKey = new PartitionKey(clusterId);
        var id = $"{entry.SiloAddress.Endpoint.Address}-{entry.SiloAddress.Endpoint.Port}-{entry.SiloAddress.Generation}";
        var membershipContainer = client.GetContainer(options.DatabaseName, options.ContainerName);
        var metadataContainer = client.GetContainer(
            options.DatabaseName,
            CosmosMembershipTable.GetMetadataContainerName(options));
        await membershipContainer.DeleteItemAsync<SiloEntity>(id, partitionKey);
        var metadataEntity = (await metadataContainer.ReadItemAsync<SiloMetadataEntity>(id, partitionKey)).Resource;
        metadataEntity.CreatedAt = DateTimeOffset.UtcNow - CosmosMembershipTable.MetadataOrphanGracePeriod - TimeSpan.FromMinutes(1);
        await metadataContainer.ReplaceItemAsync(metadataEntity, id, partitionKey);

        await membership.CleanupDefunctSiloEntries(DateTimeOffset.UtcNow);

        var exception = await Assert.ThrowsAsync<CosmosException>(
            () => metadataContainer.ReadItemAsync<SiloMetadataEntity>(id, partitionKey));
        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
    }

    private static MembershipEntry CreateMetadataEntry() => new()
    {
        SiloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 12345), 123456),
        HostName = "host",
        SiloName = "silo",
        Status = SiloStatus.Joining,
        StartTime = DateTime.UtcNow,
        IAmAliveTime = DateTime.UtcNow,
        Metadata = ImmutableDictionary<string, string>.Empty.Add("region", "west")
    };
}

[TestSuite("BVT")]
[TestProvider("Cosmos")]
[TestArea("Membership")]
public class CosmosMembershipMetadataContractTests
{
    [Fact]
    public void CompanionContainerName_IsValidAndDeterministicAtMaximumLength()
    {
        var options = new CosmosClusteringOptions { ContainerName = new string('c', 255) };

        var first = CosmosMembershipTable.GetMetadataContainerName(options);
        var second = CosmosMembershipTable.GetMetadataContainerName(options);

        Assert.Equal(first, second);
        Assert.Equal(255, first.Length);
        Assert.NotEqual(options.ContainerName, first);
    }

    [Fact]
    public void ExplicitCompanionContainerName_IsPreserved()
    {
        var options = new CosmosClusteringOptions { MetadataContainerName = "ProvisionedMetadata" };

        Assert.Equal("ProvisionedMetadata", CosmosMembershipTable.GetMetadataContainerName(options));
    }

    [Fact]
    public void CompanionContainerName_MustDifferFromMembershipContainer()
    {
        var options = new CosmosClusteringOptions
        {
            ContainerName = "Membership",
            MetadataContainerName = "Membership"
        };

        var exception = Assert.Throws<OrleansConfigurationException>(
            () => CosmosMembershipTable.GetMetadataContainerName(options));
        Assert.Contains(nameof(CosmosClusteringOptions.MetadataContainerName), exception.Message);
    }
}
