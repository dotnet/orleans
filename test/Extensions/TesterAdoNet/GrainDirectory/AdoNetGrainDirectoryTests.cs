using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySql.Data.MySqlClient;
using Npgsql;
using Orleans.Configuration;
using Orleans.GrainDirectory.AdoNet;
using Orleans.Tests.SqlUtils;
using Tester.AdoNet.Fakes;
using TestExtensions;
using UnitTests.General;
using static System.String;

namespace Tester.AdoNet.GrainDirectory;

/// <summary>
/// Tests for <see cref="AdoNetGrainDirectory"/> against SQL Server.
/// </summary>
[TestCategory("SqlServer")]
public class SqlServerAdoNetGrainDirectoryTests() : AdoNetGrainDirectoryTests(AdoNetInvariants.InvariantNameSqlServer, 90)
{
}

/// <summary>
/// Tests for <see cref="AdoNetGrainDirectory"/> against PostgreSQL.
/// </summary>
[TestCategory("PostgreSql")]
public class PostgreSqlAdoNetGrainDirectoryTests : AdoNetGrainDirectoryTests
{
    public PostgreSqlAdoNetGrainDirectoryTests() : base(AdoNetInvariants.InvariantNamePostgreSql, 90)
    {
        NpgsqlConnection.ClearAllPools();
    }
}

/// <summary>
/// Tests for <see cref="AdoNetGrainDirectory"/> against MySQL.
/// </summary>
[TestCategory("MySql")]
public class MySqlAdoNetGrainDirectoryTests : AdoNetGrainDirectoryTests
{
    public MySqlAdoNetGrainDirectoryTests() : base(AdoNetInvariants.InvariantNameMySql, 90)
    {
        MySqlConnection.ClearAllPools();
    }
}

/// <summary>
/// Tests for <see cref="AdoNetGrainDirectory"/>.
/// </summary>
[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestCategory("Functional"), TestCategory("AdoNet"), TestCategory("GrainDirectory")]
public abstract class AdoNetGrainDirectoryTests(string invariant, int concurrency = 100) : IAsyncLifetime
{
    private RelationalStorageForTesting _testing;
    private IRelationalStorage _storage;

    private const string TestDatabaseName = "OrleansGrainDirectoryTest";

    public async Task InitializeAsync()
    {
        _testing = await RelationalStorageForTesting.SetupInstance(invariant, TestDatabaseName);

        Skip.If(IsNullOrEmpty(_testing.CurrentConnectionString), $"Database '{TestDatabaseName}' not initialized");

        _storage = _testing.Storage;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Tests that a grain activation is registered.
    /// </summary>
    [SkippableFact]
    public async Task AdoNetGrainDirectory_RegistersActivation()
    {
        // arrange
        var options = new AdoNetGrainDirectoryOptions
        {
            Invariant = invariant,
            ConnectionString = _testing.CurrentConnectionString
        };
        var logger = NullLogger<AdoNetGrainDirectory>.Instance;
        var clusterOptions = Options.Create(new ClusterOptions
        {
            ClusterId = "MyClusterId"
        });
        var lifetime = new FakeHostApplicationLifetime();
        var directory = new AdoNetGrainDirectory("MyProviderId", options, logger, clusterOptions, lifetime);

        await _storage.ExecuteAsync("DELETE FROM OrleansGrainDirectory");

        // act
        var address = new GrainAddress
        {
            GrainId = GrainId.Create("MyGrainType", "MyGrainKey"),
            SiloAddress = SiloAddress.New(IPEndPoint.Parse("127.0.0.1:11111"), 123456),
            ActivationId = ActivationId.NewId()
        };
        var result = await directory.Register(address);

        // assert
        Assert.NotNull(result);
        Assert.Equal(address.GrainId, result.GrainId);
        Assert.Equal(address.SiloAddress, result.SiloAddress);
        Assert.Equal(address.ActivationId, result.ActivationId);

        var saved = await _storage.ReadAsync<AdoNetGrainDirectoryEntry>("SELECT * FROM OrleansGrainDirectory");
        var entry = Assert.Single(saved);
        Assert.Equal("MyClusterId", entry.ClusterId);
        Assert.Equal("MyProviderId", entry.ProviderId);
        Assert.Equal(address.GrainId.ToString(), entry.GrainId);
        Assert.Equal(address.SiloAddress.ToParsableString(), entry.SiloAddress);
        Assert.Equal(address.ActivationId.ToParsableString(), entry.ActivationId);
    }

    /// <summary>
    /// Tests that a grain activation is unregistered.
    /// </summary>
    [SkippableFact]
    public async Task AdoNetGrainDirectory_UnregistersActivation()
    {
        // arrange
        var options = new AdoNetGrainDirectoryOptions
        {
            Invariant = invariant,
            ConnectionString = _testing.CurrentConnectionString
        };
        var logger = NullLogger<AdoNetGrainDirectory>.Instance;
        var clusterOptions = Options.Create(new ClusterOptions
        {
            ClusterId = "MyClusterId"
        });
        var lifetime = new FakeHostApplicationLifetime();
        var directory = new AdoNetGrainDirectory("MyProviderId", options, logger, clusterOptions, lifetime);

        await _storage.ExecuteAsync("DELETE FROM OrleansGrainDirectory");

        var address = new GrainAddress
        {
            GrainId = GrainId.Create("MyGrainType", "MyGrainKey"),
            SiloAddress = SiloAddress.New(IPEndPoint.Parse("127.0.0.1:11111"), 123456),
            ActivationId = ActivationId.NewId()
        };
        await directory.Register(address);

        // act
        await directory.Unregister(address);

        // assert
        var saved = await _storage.ReadAsync<AdoNetGrainDirectoryEntry>("SELECT * FROM OrleansGrainDirectory");
        Assert.Empty(saved);
    }

    /// <summary>
    /// Tests that a grain activation can be looked up.
    /// </summary>
    [SkippableFact]
    public async Task AdoNetGrainDirectory_LooksUpActivation()
    {
        // arrange
        var options = new AdoNetGrainDirectoryOptions
        {
            Invariant = invariant,
            ConnectionString = _testing.CurrentConnectionString
        };
        var logger = NullLogger<AdoNetGrainDirectory>.Instance;
        var clusterOptions = Options.Create(new ClusterOptions
        {
            ClusterId = "MyClusterId"
        });
        var lifetime = new FakeHostApplicationLifetime();
        var directory = new AdoNetGrainDirectory("MyProviderId", options, logger, clusterOptions, lifetime);

        await _storage.ExecuteAsync("DELETE FROM OrleansGrainDirectory");

        var grainId = GrainId.Create("MyGrainType", "MyGrainKey");
        var siloAddress = SiloAddress.New(IPEndPoint.Parse("127.0.0.1:11111"), 123456);
        var activationId = ActivationId.NewId();
        var address = new GrainAddress
        {
            GrainId = grainId,
            SiloAddress = siloAddress,
            ActivationId = activationId
        };
        await directory.Register(address);

        // act
        var result = await directory.Lookup(grainId);

        // assert
        Assert.NotNull(result);
        Assert.Equal(grainId, result.GrainId);
        Assert.Equal(siloAddress, result.SiloAddress);
        Assert.Equal(activationId, result.ActivationId);
    }

    /// <summary>
    /// Tests that grain activations can be unregistered for a set of silos.
    /// </summary>
    [SkippableFact]
    public async Task AdoNetGrainDirectory_UnregistersActivationsForSilos()
    {
        // arrange
        var options = new AdoNetGrainDirectoryOptions
        {
            Invariant = invariant,
            ConnectionString = _testing.CurrentConnectionString
        };
        var logger = NullLogger<AdoNetGrainDirectory>.Instance;
        var clusterOptions = Options.Create(new ClusterOptions
        {
            ClusterId = "MyClusterId"
        });
        var lifetime = new FakeHostApplicationLifetime();
        var directory = new AdoNetGrainDirectory("MyProviderId", options, logger, clusterOptions, lifetime);

        await _storage.ExecuteAsync("DELETE FROM OrleansGrainDirectory");

        var grainIds = Enumerable.Range(0, 5).Select(i => GrainId.Create("MyGrainType", $"MyGrainKey{i}")).ToArray();
        var siloAddresses = Enumerable.Range(0, 5).Select(i => SiloAddress.New(IPEndPoint.Parse($"127.0.0.{i}:11111"), 123456)).ToArray();
        var activationIds = Enumerable.Range(0, 5).Select(_ => ActivationId.NewId()).ToArray();
        var addresses = Enumerable.Range(0, 5).Select(i => new GrainAddress
        {
            GrainId = grainIds[i],
            SiloAddress = siloAddresses[i],
            ActivationId = activationIds[i]
        }).ToArray();

        for (var i = 0; i < 5; i++)
        {
            await directory.Register(addresses[i]);
        }

        // act
        await directory.UnregisterSilos([siloAddresses[0], siloAddresses[2], siloAddresses[4]]);

        // assert
        var results = await _storage.ReadAsync<AdoNetGrainDirectoryEntry>("SELECT * FROM OrleansGrainDirectory ORDER BY GrainId");
        var resultAddresses = results.Select(entry => entry.ToGrainAddress()).ToArray();
        Assert.Equal([addresses[1], addresses[3]], resultAddresses);
    }

    /// <summary>
    /// Tests that a grain activation can be looked up.
    /// </summary>
    [SkippableFact]
    public async Task AdoNetGrainDirectory_ChaosTest()
    {
        // arrange
        var options = new AdoNetGrainDirectoryOptions
        {
            Invariant = invariant,
            ConnectionString = _testing.CurrentConnectionString
        };
        var logger = NullLogger<AdoNetGrainDirectory>.Instance;
        var clusterOptions = Options.Create(new ClusterOptions
        {
            ClusterId = "MyClusterId"
        });
        var lifetime = new FakeHostApplicationLifetime();
        var directory = new AdoNetGrainDirectory("MyProviderId", options, logger, clusterOptions, lifetime);

        await _storage.ExecuteAsync("DELETE FROM OrleansGrainDirectory");

        // act
        await Parallel.ForAsync(0, 10000, new ParallelOptions { MaxDegreeOfParallelism = concurrency }, async (i, ct) =>
        {
            var grainId = GrainId.Create("MyGrainType", $"MyGrainKey{Random.Shared.Next(10)}");
            var siloAddress = SiloAddress.New(IPEndPoint.Parse($"127.0.0.{Random.Shared.Next(10)}:11111"), 123456);
            var activationId = ActivationId.NewId();
            var address = new GrainAddress
            {
                GrainId = grainId,
                SiloAddress = siloAddress,
                ActivationId = activationId
            };
            var lookups = Random.Shared.Next(10);

            // simulate the lifecycle including unstable overlapping operations
            var registered = await directory.Register(address);
            for (var j = 0; j < lookups; j++)
            {
                var result = await directory.Lookup(grainId);
            }

            // it is possible that the registration failed due to some other concurrent operation
            if (registered is not null)
            {
                await directory.Unregister(registered);
            }
        });

        // assert
        var remaining = await _storage.ReadAsync<AdoNetGrainDirectoryEntry>("SELECT * FROM OrleansGrainDirectory");
        Assert.Empty(remaining);
    }
}