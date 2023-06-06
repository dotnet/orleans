using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UnitTests;
using Orleans.Messaging;
using UnitTests.MembershipTests;
using Orleans.Clustering.GoogleFirestore;

namespace Orleans.Tests.Google;

[TestCategory("Functional"), TestCategory("GoogleFirestore"), TestCategory("GoogleCloud")]
public class FirestoreMembershipTableTests : MembershipTableTestsBase, IClassFixture<GoogleCloudFixture>
{
    private const string PROJECT_ID = "orleans-test";

    public FirestoreMembershipTableTests(
        ConnectionStringFixture csFixture,
        GoogleCloudFixture googleFixture) : base(csFixture, googleFixture, CreateFilters())
    {
    }

    private static LoggerFilterOptions CreateFilters()
    {
        var filters = new LoggerFilterOptions();
        filters.AddFilter("FirestoreDataManager", LogLevel.Trace);
        filters.AddFilter("OrleansSiloInstanceManager", LogLevel.Trace);
        filters.AddFilter("Storage", LogLevel.Trace);
        filters.AddFilter("GoogleFirestoreMembershipTable", LogLevel.Trace);
        return filters;
    }

    protected override IMembershipTable CreateMembershipTable(ILogger logger)
    {
        var options = new FirestoreOptions
        {
            ProjectId = PROJECT_ID,
            EmulatorHost = (this.environment as GoogleCloudFixture)!.Emulator.FirestoreEndpoint
        };

        return new GoogleFirestoreMembershipTable(this.loggerFactory, Options.Create(options), this.clusterOptions);
    }

    protected override IGatewayListProvider CreateGatewayListProvider(ILogger logger)
    {
        var options = new FirestoreOptions
        {
            ProjectId = PROJECT_ID,
            EmulatorHost = (this.environment as GoogleCloudFixture)!.Emulator.FirestoreEndpoint
        };

        return new GoogleFirestoreGatewayListProvider(this.loggerFactory, Options.Create(options), this.clusterOptions, this.gatewayOptions);
    }

    protected override Task<string> GetConnectionString() => Task.FromResult("<dummy>");

    [Fact]
    public Task GetGateways() => MembershipTable_GetGateways();

    [Fact]
    public Task ReadAll_EmptyTable() => MembershipTable_ReadAll_EmptyTable();

    [Fact]
    public Task InsertRow() => MembershipTable_InsertRow();

    [Fact]
    public Task ReadRow_Insert_Read() => MembershipTable_ReadRow_Insert_Read();

    [Fact]
    public Task ReadAll_Insert_ReadAll() => MembershipTable_ReadAll_Insert_ReadAll();

    [Fact]
    public Task UpdateRow() => MembershipTable_UpdateRow();

    [Fact]
    public Task CleanupDefunctSiloEntries() => MembershipTable_CleanupDefunctSiloEntries();

    [Fact]
    public Task UpdateRowInParallel() => MembershipTable_UpdateRowInParallel();

    [Fact]
    public Task UpdateIAmAlive() => MembershipTable_UpdateIAmAlive();
}