using System.Security.Cryptography;
using System.Text;
using Orleans.Tests.SqlUtils;
using UnitTests.General;
using Xunit;

namespace UnitTests.StorageTests.Relational;

/// <summary>
/// Verifies that the persistence migrations upgrade databases created by earlier persistence scripts.
/// </summary>
[TestSuite("Functional")]
[TestArea("Persistence")]
public sealed class AdoNetPersistenceMigrationTests(CommonFixture fixture) : IClassFixture<CommonFixture>
{
    // src/AdoNet/Orleans.Persistence.AdoNet/*-Persistence.sql from 3a33a53af4679ed10b075608c7caecea018b6892,
    // the last scripts before the 10.5.0 migrations. Hashes normalize checkout line endings.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [TestProvider("SqlServer")]
    public Task SqlServer_Migration_10_5_0_WrapsVersion(bool deleteStateOnClear) =>
        VerifyVersionWrapMigrationAsync("SQLServer", AdoNetInvariants.InvariantNameSqlServer, deleteStateOnClear,
            "51CA6BD2E65B9328F4E012B07927CEA933A5120BCC5EEFEDC3803BD3AA460CD7");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [TestProvider("PostgreSql")]
    public Task PostgreSql_Migration_10_5_0_WrapsVersion(bool deleteStateOnClear) =>
        VerifyVersionWrapMigrationAsync("PostgreSQL", AdoNetInvariants.InvariantNamePostgreSql, deleteStateOnClear,
            "5C8A808D5A475E3C53841CC21DDDB67E0E5B4B69A7BDC3B34E8CB1DE3F9A1F4D");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [TestProvider("MySql")]
    public Task MySql_Migration_10_5_0_WrapsVersion(bool deleteStateOnClear) =>
        VerifyVersionWrapMigrationAsync("MySQL", AdoNetInvariants.InvariantNameMySql, deleteStateOnClear,
            "2A40E92DD1D6413389EB3BD8353394036C9032F66E5CBE118C76133FD9A76C4D");

    private async Task VerifyVersionWrapMigrationAsync(string engine, string invariant, bool deleteStateOnClear, string fixtureHash)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "PersistenceUpgradeFixtures", $"{engine}-Persistence.sql");
        var original = await File.ReadAllTextAsync(fixturePath, cancellationToken);
        Assert.Equal(fixtureHash, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(original.Replace("\r\n", "\n")))));

        // The migration runs twice to show that repeating it leaves an upgraded database working.
        var migrationPath = Path.Combine(AppContext.BaseDirectory, $"{engine}-Persistence-10.5.0.sql");
        var database = await RelationalStorageForTesting.SetupInstance(
            invariant,
            $"PersistenceMigration_{Guid.NewGuid():N}",
            cancellationToken: cancellationToken,
            setupSqlScriptFileNames: [Path.Combine(AppContext.BaseDirectory, $"{engine}-Main.sql"), fixturePath, migrationPath, migrationPath]);
        var storage = await fixture.CreateStorageProviderAsync(invariant, database.CurrentConnectionString, deleteStateOnClear, cancellationToken);

        await GrainStorageVersionWrapTests.VersionWrapsAfterMaximum(storage, database.Storage, deleteStateOnClear, cancellationToken);
    }
}
