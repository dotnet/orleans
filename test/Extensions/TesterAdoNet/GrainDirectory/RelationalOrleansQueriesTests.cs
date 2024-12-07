using Orleans.GrainDirectory.AdoNet;
using Orleans.GrainDirectory.AdoNet.Storage;
using UnitTests.General;
using static System.String;

namespace Tester.AdoNet.GrainDirectory;

/// <summary>
/// Tests the relational storage layer via <see cref="RelationalOrleansQueries"/> against Sql Server.
/// </summary>
public class SqlServerRelationalOrleansQueriesTests() : RelationalOrleansQueriesTests(AdoNetInvariants.InvariantNameSqlServer, 90)
{
}

/*
/// <summary>
/// Tests the relational storage layer via <see cref="RelationalOrleansQueries"/> against MySQL.
/// </summary>
public class MySqlRelationalOrleansQueriesTests : RelationalOrleansQueriesTests
{
    public MySqlRelationalOrleansQueriesTests() : base(AdoNetInvariants.InvariantNameMySql, 100)
    {
        MySqlConnection.ClearAllPools();
    }
}
*/

/*
/// <summary>
/// Tests the relational storage layer via <see cref="RelationalOrleansQueries"/> against PostgreSQL.
/// </summary>
public class PostgreSqlRelationalOrleansQueriesTests : RelationalOrleansQueriesTests
{
    public PostgreSqlRelationalOrleansQueriesTests() : base(AdoNetInvariants.InvariantNamePostgreSql, 99)
    {
        NpgsqlConnection.ClearAllPools();
    }
}
*/

/// <summary>
/// Tests the relational storage layer via <see cref="RelationalOrleansQueries"/>.
/// </summary>
[TestCategory("AdoNet"), TestCategory("GrainDirectory")]
public abstract class RelationalOrleansQueriesTests(string invariant, int concurrency = 100) : IAsyncLifetime
{
    private const string TestDatabaseName = "OrleansGrainDirectoryTest";

    private IRelationalStorage _storage;
    private RelationalOrleansQueries _queries;

    public async Task InitializeAsync()
    {
        var testing = await RelationalStorageForTesting.SetupInstance(invariant, TestDatabaseName);
        Skip.If(IsNullOrEmpty(testing.CurrentConnectionString), $"Database '{TestDatabaseName}' not initialized");

        _storage = RelationalStorage.CreateInstance(invariant, testing.CurrentConnectionString);

        _queries = await RelationalOrleansQueries.CreateInstance(invariant, testing.CurrentConnectionString);
    }

    private static string RandomClusterId(int max = 10) => $"ClusterId{Random.Shared.Next(max)}";

    private static string RandomProviderId(int max = 10) => $"ProviderId{Random.Shared.Next(max)}";

    private static string RandomGrainId(int max = 10) => $"GrainId{Random.Shared.Next(max)}";

    private static string RandomSiloAddress(int max = 10) => $"SiloAddress{Random.Shared.Next(max)}";

    private static string RandomActivationId(int max = 10) => $"ActivationId{Random.Shared.Next(max)}";

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Tests that a grain activation is registered.
    /// </summary>
    [SkippableFact]
    public async Task RelationalOrleansQueries_RegistersActivation()
    {
        // arrange
        var clusterId = RandomClusterId();
        var providerId = RandomProviderId();
        var grainId = RandomGrainId();
        var siloAddress = RandomSiloAddress();
        var activationId = RandomActivationId();

        await _storage.ExecuteAsync("DELETE FROM OrleansGrainDirectory");

        // act
        var count = await _queries.RegisterGrainActivationAsync(clusterId, providerId, grainId, siloAddress, activationId);

        // assert
        Assert.Equal(1, count);

        var results = await _storage.ReadAsync<AdoNetGrainDirectoryEntry>("SELECT * FROM OrleansGrainDirectory");
        var result = Assert.Single(results);
        Assert.Equal(clusterId, result.ClusterId);
        Assert.Equal(providerId, result.ProviderId);
        Assert.Equal(grainId, result.GrainId);
        Assert.Equal(siloAddress, result.SiloAddress);
        Assert.Equal(activationId, result.ActivationId);
    }

    /// <summary>
    /// Tests that a grain activation is unregistered.
    /// </summary>
    [SkippableFact]
    public async Task RelationalOrleansQueries_UnregistersActivation()
    {
        // arrange
        var clusterId = RandomClusterId();
        var providerId = RandomProviderId();
        var grainId = RandomGrainId();
        var siloAddress = RandomSiloAddress();
        var activationId = RandomActivationId();

        await _storage.ExecuteAsync("DELETE FROM OrleansGrainDirectory");

        // act
        var count1 = await _queries.RegisterGrainActivationAsync(clusterId, providerId, grainId, siloAddress, activationId);
        var count2 = await _queries.UnregisterGrainActivationAsync(clusterId, providerId, grainId, activationId);

        // assert
        Assert.Equal(1, count1);
        Assert.Equal(1, count2);

        var results = await _storage.ReadAsync<AdoNetGrainDirectoryEntry>("SELECT * FROM OrleansGrainDirectory");
        Assert.Empty(results);
    }

    /// <summary>
    /// Tests that a grain activation can be looked up.
    /// </summary>
    [SkippableFact]
    public async Task RelationalOrleansQueries_LooksUpActivation()
    {
        // arrange
        var clusterId = RandomClusterId();
        var providerId = RandomProviderId();
        var grainId = RandomGrainId();
        var siloAddress = RandomSiloAddress();
        var activationId = RandomActivationId();

        await _storage.ExecuteAsync("DELETE FROM OrleansGrainDirectory");

        // act
        var count = await _queries.RegisterGrainActivationAsync(clusterId, providerId, grainId, siloAddress, activationId);
        var result = await _queries.LookupGrainActivationAsync(clusterId, providerId, grainId);

        // assert
        Assert.Equal(1, count);
        Assert.NotNull(result);
        Assert.Equal(clusterId, result.ClusterId);
        Assert.Equal(providerId, result.ProviderId);
        Assert.Equal(grainId, result.GrainId);
        Assert.Equal(siloAddress, result.SiloAddress);
        Assert.Equal(activationId, result.ActivationId);
    }

    /// <summary>
    /// Tests that grain activations for a set of silos are unregistered.
    /// </summary>
    [SkippableFact]
    public async Task RelationalOrleansQueries_UnregistersActivationsForSilos()
    {
        // arrange
        var clusterId = RandomClusterId();
        var providerId = RandomProviderId();
        var grainId = RandomGrainId();
        var activationId = RandomActivationId();

        await _storage.ExecuteAsync("DELETE FROM OrleansGrainDirectory");

        await _queries.RegisterGrainActivationAsync(clusterId, providerId, "G1", "A", "A1");
        await _queries.RegisterGrainActivationAsync(clusterId, providerId, "G2", "B", "A2");
        await _queries.RegisterGrainActivationAsync(clusterId, providerId, "G3", "C", "A3");
        await _queries.RegisterGrainActivationAsync(clusterId, providerId, "G4", "D", "A4");
        await _queries.RegisterGrainActivationAsync(clusterId, providerId, "G5", "E", "A5");

        // act
        var count = await _queries.UnregisterGrainActivationsAsync(clusterId, providerId, "A|C|E");

        // assert
        Assert.Equal(3, count);

        var remaining = await _storage.ReadAsync<AdoNetGrainDirectoryEntry>("SELECT * FROM OrleansGrainDirectory");
        Assert.Collection(remaining.OrderBy(x => x.GrainId),
            entry => Assert.Equal("G2", entry.GrainId),
            entry => Assert.Equal("G4", entry.GrainId));
    }

    /// <summary>
    /// Chaos test for concurrent grain directory queries.
    /// </summary>
    /// <remarks>
    /// This test looks for susceptibility to database deadlocks and other concurrency issues.
    /// During development, this test consistently triggered deadlocking until the queries were made to prevent it.
    /// If this test shows flakiness then it is likely the queries need to be looked at.
    /// </remarks>
    [SkippableFact]
    public async Task RelationalOrleansQueries_ChaosTest()
    {
        var clusterId = RandomClusterId();
        var providerId = RandomProviderId();

        // act
        await Parallel.ForAsync(0, 10000, new ParallelOptions { MaxDegreeOfParallelism = concurrency }, async (x, ct) =>
        {
            var grainId = RandomGrainId();
            var siloAddress = RandomSiloAddress();
            var activationId = RandomActivationId();
            var randomLookups = Random.Shared.Next(10);

            // simulate the lifecycle including unstable overlapping operations
            await _queries.RegisterGrainActivationAsync(clusterId, providerId, grainId, siloAddress, activationId);
            for (var i = 0; i < randomLookups; i++)
            {
                await _queries.LookupGrainActivationAsync(clusterId, providerId, grainId);
            }
            await _queries.UnregisterGrainActivationAsync(clusterId, providerId, grainId, activationId);
        });

        // assert
        var remaining = await _storage.ReadAsync<AdoNetGrainDirectoryEntry>("SELECT * FROM OrleansGrainDirectory");
        Assert.Empty(remaining);
    }
}