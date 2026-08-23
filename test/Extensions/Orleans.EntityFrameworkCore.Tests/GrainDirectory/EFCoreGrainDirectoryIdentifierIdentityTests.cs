using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.EntityFrameworkCore.Tests.Infrastructure;
using Orleans.GrainDirectory.EntityFrameworkCore.Data;
using Orleans.GrainDirectory.EntityFrameworkCore.MySql.Data;
using Orleans.GrainDirectory.EntityFrameworkCore.SqlServer.Data;
using Orleans.Runtime;
using TestExtensions;

namespace Orleans.EntityFrameworkCore.Tests.GrainDirectory;

[TestArea("EFCore")]
[TestSuite("Functional")]
[TestProvider(EFCoreTestCategories.MySqlProvider)]
[TestArea("GrainDirectory")]
public sealed class MySqlGrainDirectoryIdentifierIdentityTests
{
    private readonly ITestOutputHelper _testOutput;

    public MySqlGrainDirectoryIdentifierIdentityTests(ITestOutputHelper testOutput)
    {
        _testOutput = testOutput;
    }

    [Fact]
    public Task PR8654_GrainDirectory_TrailingSpaceIdentifiersRemainDistinct() =>
        GrainDirectoryIdentifierIdentityTest.Run<
            MySqlGrainDirectoryDbContext,
            Guid,
            MySqlEFCoreProviderConfiguration>(_testOutput);
}

[TestArea("EFCore")]
[TestSuite("Functional")]
[TestProvider(EFCoreTestCategories.SqlServer)]
[TestArea("GrainDirectory")]
public sealed class SqlServerGrainDirectoryIdentifierIdentityTests
{
    private readonly ITestOutputHelper _testOutput;

    public SqlServerGrainDirectoryIdentifierIdentityTests(ITestOutputHelper testOutput)
    {
        _testOutput = testOutput;
    }

    [Fact]
    public Task PR8654_GrainDirectory_TrailingSpaceIdentifiersRemainDistinct() =>
        GrainDirectoryIdentifierIdentityTest.Run<
            SqlServerGrainDirectoryDbContext,
            byte[],
            SqlServerEFCoreProviderConfiguration>(_testOutput);
}

internal static class GrainDirectoryIdentifierIdentityTest
{
    public static async Task Run<TDbContext, TETag, TProvider>(ITestOutputHelper testOutput)
        where TDbContext : GrainDirectoryDbContext<TDbContext, TETag>
        where TProvider : EFCoreProviderConfiguration<TETag>, new()
    {
        var provider = new TProvider();
        await using var databaseFixture = new EFCoreDatabaseFixture<TDbContext>(
            provider.Database,
            "directory_identity",
            $"{typeof(TDbContext).Name}_{GetTargetFramework()}",
            writeOutput: testOutput.WriteLine);
        await databaseFixture.InitializeAsync();

        var clusterId = $"identity-{Guid.NewGuid():N}";
        var directory = new EFCoreGrainDirectory<TDbContext, TETag>(
            NullLoggerFactory.Instance,
            databaseFixture.Factory,
            Options.Create(new ClusterOptions
            {
                ClusterId = clusterId,
                ServiceId = $"identity-{Guid.NewGuid():N}"
            }),
            provider.CreateGrainDirectoryETagConverter());

        const string unspacedIdentifier = "directory-identity/directory-key";
        const string spacedIdentifier = "directory-identity/directory-key ";
        var unspacedId = GrainId.Parse(unspacedIdentifier);
        var spacedId = GrainId.Parse(spacedIdentifier);
        Assert.NotEqual(unspacedId, spacedId);
        Assert.Equal(unspacedIdentifier, unspacedId.ToString());
        Assert.Equal(spacedIdentifier, spacedId.ToString());

        var unspacedFirst = CreateAddress(
            unspacedId,
            "00112233-4455-6677-8899-aabbccddeeff",
            "10.31.1.11:3011@4011",
            101);
        var spacedFirst = CreateAddress(
            spacedId,
            "10213243-5465-7687-98a9-bacbdcedfe0f",
            "10.31.1.12:3012@4012",
            202);

        AssertAddress(unspacedFirst, await directory.Register(unspacedFirst));
        AssertAddress(spacedFirst, await directory.Register(spacedFirst));

        await using (var context = await databaseFixture.Factory.CreateDbContextAsync())
        {
            var inserted = (await context.Activations.AsNoTracking().ToListAsync())
                .OrderBy(record => record.GrainId, StringComparer.Ordinal)
                .ToList();
            Assert.Equal(2, inserted.Count);
            AssertRecord(clusterId, unspacedIdentifier, unspacedFirst, inserted[0]);
            AssertRecord(clusterId, spacedIdentifier, spacedFirst, inserted[1]);
        }

        AssertAddress(unspacedFirst, await directory.Lookup(unspacedId));
        AssertAddress(spacedFirst, await directory.Lookup(spacedId));

        var unspacedUpdated = CreateAddress(
            unspacedId,
            "20314253-6475-8697-a8b9-cadbecfd0e1f",
            "10.31.2.21:3021@4021",
            303);
        var spacedUpdated = CreateAddress(
            spacedId,
            "30415263-7485-96a7-b8c9-daebfc0d1e2f",
            "10.31.2.22:3022@4022",
            404);

        AssertAddress(unspacedUpdated, await directory.Register(unspacedUpdated, unspacedFirst));
        AssertAddress(spacedUpdated, await directory.Register(spacedUpdated, spacedFirst));
        AssertAddress(unspacedUpdated, await directory.Lookup(unspacedId));
        AssertAddress(spacedUpdated, await directory.Lookup(spacedId));

        await using (var context = await databaseFixture.Factory.CreateDbContextAsync())
        {
            var updated = (await context.Activations.AsNoTracking().ToListAsync())
                .OrderBy(record => record.GrainId, StringComparer.Ordinal)
                .ToList();
            Assert.Equal(2, updated.Count);
            AssertRecord(clusterId, unspacedIdentifier, unspacedUpdated, updated[0]);
            AssertRecord(clusterId, spacedIdentifier, spacedUpdated, updated[1]);
        }

        await directory.Unregister(unspacedUpdated);

        Assert.Null(await directory.Lookup(unspacedId));
        AssertAddress(spacedUpdated, await directory.Lookup(spacedId));
        await using (var context = await databaseFixture.Factory.CreateDbContextAsync())
        {
            var survivor = Assert.Single(await context.Activations.AsNoTracking().ToListAsync());
            AssertRecord(clusterId, spacedIdentifier, spacedUpdated, survivor);
        }

        await directory.Unregister(spacedUpdated);

        Assert.Null(await directory.Lookup(spacedId));
        await using (var context = await databaseFixture.Factory.CreateDbContextAsync())
        {
            Assert.Empty(await context.Activations.AsNoTracking().ToListAsync());
        }
    }

    private static GrainAddress CreateAddress(
        GrainId grainId,
        string activationId,
        string siloAddress,
        long membershipVersion) =>
        new()
        {
            ActivationId = new ActivationId(new Guid(activationId)),
            GrainId = grainId,
            SiloAddress = SiloAddress.FromParsableString(siloAddress),
            MembershipVersion = new MembershipVersion(membershipVersion)
        };

    private static void AssertAddress(GrainAddress expected, GrainAddress? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.GrainId, actual.GrainId);
        Assert.Equal(expected.ActivationId, actual.ActivationId);
        Assert.Equal(expected.SiloAddress, actual.SiloAddress);
        Assert.Equal(expected.MembershipVersion, actual.MembershipVersion);
    }

    private static void AssertRecord<TETag>(
        string expectedClusterId,
        string expectedGrainId,
        GrainAddress expectedAddress,
        GrainActivationRecord<TETag> actual)
    {
        Assert.Equal(expectedClusterId, actual.ClusterId);
        Assert.Equal(expectedGrainId, actual.GrainId);
        Assert.Equal(expectedAddress.ActivationId.ToParsableString(), actual.ActivationId);
        Assert.Equal(expectedAddress.SiloAddress!.ToParsableString(), actual.SiloAddress);
        Assert.Equal(expectedAddress.MembershipVersion.Value, actual.MembershipVersion);
    }

    private static string GetTargetFramework()
    {
#if NET8_0
        return "net8";
#elif NET10_0
        return "net10";
#else
        return "unknown";
#endif
    }
}
