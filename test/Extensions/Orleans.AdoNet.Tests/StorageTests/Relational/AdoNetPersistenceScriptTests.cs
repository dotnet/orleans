using Orleans.Runtime;
using Orleans.Tests.SqlUtils;
using UnitTests.General;
using UnitTests.StorageTests.Relational.TestDataSets;
using Xunit;

namespace UnitTests.StorageTests.Relational;

/// <summary>
/// Verifies that the canonical persistence scripts preserve state and refresh queries on repeatable upgrades.
/// </summary>
[TestSuite("Functional")]
[TestArea("Persistence")]
public sealed class AdoNetPersistenceScriptTests(CommonFixture fixture) : IClassFixture<CommonFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [TestProvider("SqlServer")]
    public Task SqlServer_PersistenceScript_UpgradesRepeatedly(bool deleteStateOnClear) =>
        VerifyRepeatableUpgradeAsync("SQLServer", AdoNetInvariants.InvariantNameSqlServer, deleteStateOnClear);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [TestProvider("PostgreSql")]
    public Task PostgreSql_PersistenceScript_UpgradesRepeatedly(bool deleteStateOnClear) =>
        VerifyRepeatableUpgradeAsync("PostgreSQL", AdoNetInvariants.InvariantNamePostgreSql, deleteStateOnClear);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [TestProvider("MySql")]
    public Task MySql_PersistenceScript_UpgradesRepeatedly(bool deleteStateOnClear) =>
        VerifyRepeatableUpgradeAsync("MySQL", AdoNetInvariants.InvariantNameMySql, deleteStateOnClear);

    private async Task VerifyRepeatableUpgradeAsync(string engine, string invariant, bool deleteStateOnClear)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var persistencePath = Path.Combine(AppContext.BaseDirectory, $"{engine}-Persistence.sql");
        var script = await File.ReadAllTextAsync(persistencePath, cancellationToken);
        var databaseName = $"PersistenceUpgrade_{Guid.NewGuid():N}";
        var database = await RelationalStorageForTesting.SetupInstance(
            invariant,
            databaseName,
            cancellationToken: cancellationToken,
            setupSqlScriptFileNames: [Path.Combine(AppContext.BaseDirectory, $"{engine}-Main.sql"), persistencePath]);
        await database.Storage.ExecuteAsync(
            "INSERT INTO OrleansQuery (QueryKey, QueryText) VALUES ('UnrelatedPersistenceScriptTest', 'preserve')",
            cancellationToken);
        var expectedQueries = await ReadQueriesAsync(database.Storage, cancellationToken);

        // The schema is unchanged. Install the previous increment to exercise replacement of existing routines and queries.
        await ApplyScriptAsync(database, script.Replace(" % 2147483647", "", StringComparison.Ordinal), cancellationToken);
        var serviceId = Guid.NewGuid().ToString();
        var originalStorage = await fixture.CreateStorageProviderAsync(
            invariant, database.CurrentConnectionString, serviceId, deleteStateOnClear, cancellationToken);
        var grainId = GrainId.Create("repeatable-upgrade", Guid.NewGuid().ToString());
        const string grainType = "repeatable-upgrade-grain";
        var original = new GrainState<TestState1> { State = new TestState1 { A = "before-upgrade" } };
        await originalStorage.WriteStateAsync(grainType, grainId, original, cancellationToken);
        await database.Storage.ExecuteAsync(
            "UPDATE OrleansQuery SET QueryText = 'outdated' WHERE QueryKey IN ('WriteToStorageKey', 'ReadFromStorageKey', 'ClearStorageKey', 'DeleteStorageKey')",
            cancellationToken);

        for (var iteration = 0; iteration < 2; iteration++)
        {
            await ApplyScriptAsync(database, script, cancellationToken);
            var actualQueries = await ReadQueriesAsync(database.Storage, cancellationToken);
            Assert.Equal(expectedQueries.OrderBy(query => query.Key), actualQueries.OrderBy(query => query.Key));
            var preserved = new GrainState<TestState1>();
            await originalStorage.ReadStateAsync(grainType, grainId, preserved, cancellationToken);
            Assert.True(preserved.RecordExists);
            Assert.Equal("1", preserved.ETag);
            Assert.Equal(new TestState1 { A = "before-upgrade" }, preserved.State);
        }

        var storage = await fixture.CreateStorageProviderAsync(
            invariant, database.CurrentConnectionString, serviceId, deleteStateOnClear, cancellationToken);
        var upgraded = new GrainState<TestState1>();
        await storage.ReadStateAsync(grainType, grainId, upgraded, cancellationToken);
        Assert.Equal("1", upgraded.ETag);
        Assert.Equal(new TestState1 { A = "before-upgrade" }, upgraded.State);
        upgraded.State = new TestState1 { A = "after-upgrade" };
        await storage.WriteStateAsync(grainType, grainId, upgraded, cancellationToken);
        var oldReader = new GrainState<TestState1>();
        await originalStorage.ReadStateAsync(grainType, grainId, oldReader, cancellationToken);
        Assert.Equal("2", oldReader.ETag);
        Assert.Equal(new TestState1 { A = "after-upgrade" }, oldReader.State);
        await GrainStorageVersionWrapTests.VersionWrapsAfterMaximum(storage, database.Storage, deleteStateOnClear, cancellationToken, originalStorage);
    }

    private static async Task ApplyScriptAsync(RelationalStorageForTesting database, string script, CancellationToken cancellationToken)
    {
        IEnumerable<string> batches = database is MySqlStorageForTesting mySql ? mySql.SplitScript(script) : [script];
        foreach (var batch in batches)
        {
            await database.Storage.ExecuteAsync(batch, cancellationToken);
        }
    }

    private static async Task<Dictionary<string, string>> ReadQueriesAsync(IRelationalStorage storage, CancellationToken cancellationToken)
    {
        var queries = await storage.ReadAsync(
            "SELECT QueryKey, QueryText FROM OrleansQuery",
            command => { },
            (record, _, _) => Task.FromResult(KeyValuePair.Create(record.GetString(0), record.GetString(1))),
            cancellationToken: cancellationToken);
        return queries.ToDictionary(query => query.Key, query => query.Value, StringComparer.Ordinal);
    }
}
