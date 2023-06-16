using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UnitTests;
using TestExtensions;
using Orleans.Messaging;
using UnitTests.MembershipTests;
using Orleans.Clustering.GoogleFirestore;

namespace Orleans.Tests.Google;

[TestCategory("Functional"), TestCategory("GoogleFirestore"), TestCategory("GoogleCloud")]
public class FirestoreMembershipTableTests : MembershipTableTestsBase, IClassFixture<TestEnvironmentFixture>
{
    public FirestoreMembershipTableTests(
        ConnectionStringFixture csFixture,
        TestEnvironmentFixture environment) : base(csFixture, environment, CreateFilters())
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
            ProjectId = "orleans-test",
            EmulatorHost = GoogleEmulatorHost.FirestoreEndpoint
        };

        return new GoogleFirestoreMembershipTable(this.loggerFactory, Options.Create(options), this._clusterOptions);
    }

    protected override IGatewayListProvider CreateGatewayListProvider(ILogger logger)
    {
        var options = new FirestoreOptions
        {
            ProjectId = GoogleEmulatorHost.ProjectId,
            EmulatorHost = GoogleEmulatorHost.FirestoreEndpoint
        };

        return new GoogleFirestoreGatewayListProvider(this.loggerFactory, Options.Create(options), this._clusterOptions, this._gatewayOptions);
    }

    protected override Task<string> GetConnectionString() => Task.FromResult("<dummy>");

    [SkippableFact]
    public Task GetGateways() => MembershipTable_GetGateways();

    [SkippableFact]
    public Task ReadAll_EmptyTable() => MembershipTable_ReadAll_EmptyTable();

    [SkippableFact]
    public Task InsertRow() => MembershipTable_InsertRow();

    [SkippableFact]
    public Task ReadRow_Insert_Read() => MembershipTable_ReadRow_Insert_Read();

    [SkippableFact]
    public Task ReadAll_Insert_ReadAll() => MembershipTable_ReadAll_Insert_ReadAll();

    [SkippableFact]
    public Task UpdateRow() => MembershipTable_UpdateRow();

    [SkippableFact]
    public Task CleanupDefunctSiloEntries() => MembershipTable_CleanupDefunctSiloEntries();

    [SkippableFact]
    public Task UpdateRowInParallel() => MembershipTable_UpdateRowInParallel();

    [SkippableFact]
    public Task UpdateIAmAlive() => MembershipTable_UpdateIAmAlive();
}