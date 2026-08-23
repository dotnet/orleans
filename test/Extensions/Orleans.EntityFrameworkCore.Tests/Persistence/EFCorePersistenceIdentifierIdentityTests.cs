using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.EntityFrameworkCore.Tests.Infrastructure;
using Orleans.Persistence.EntityFrameworkCore.Data;
using Orleans.Persistence.EntityFrameworkCore.MySql.Data;
using Orleans.Persistence.EntityFrameworkCore.SqlServer.Data;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Storage;
using TestExtensions;

namespace Orleans.EntityFrameworkCore.Tests.Persistence;

[TestArea("EFCore")]
[TestSuite("Functional")]
[TestProvider(EFCoreTestCategories.MySqlProvider)]
[TestArea("Persistence")]
public sealed class MySqlPersistenceIdentifierIdentityTests
{
    private readonly ITestOutputHelper _testOutput;

    public MySqlPersistenceIdentifierIdentityTests(ITestOutputHelper testOutput)
    {
        _testOutput = testOutput;
    }

    [Fact]
    public Task PR8654_Persistence_TrailingSpaceIdentifiersRemainDistinct() =>
        PersistenceIdentifierIdentityTest.Run<
            MySqlGrainStateDbContext,
            Guid,
            MySqlEFCoreProviderConfiguration>(_testOutput);
}

[TestArea("EFCore")]
[TestSuite("Functional")]
[TestProvider(EFCoreTestCategories.SqlServer)]
[TestArea("Persistence")]
public sealed class SqlServerPersistenceIdentifierIdentityTests
{
    private readonly ITestOutputHelper _testOutput;

    public SqlServerPersistenceIdentifierIdentityTests(ITestOutputHelper testOutput)
    {
        _testOutput = testOutput;
    }

    [Fact]
    public Task PR8654_Persistence_TrailingSpaceIdentifiersRemainDistinct() =>
        PersistenceIdentifierIdentityTest.Run<
            SqlServerGrainStateDbContext,
            byte[],
            SqlServerEFCoreProviderConfiguration>(_testOutput);
}

internal static class PersistenceIdentifierIdentityTest
{
    private const string StorageName = "PersistenceIdentifierIdentity";

    public static async Task Run<TDbContext, TETag, TProvider>(ITestOutputHelper testOutput)
        where TDbContext : GrainStateDbContext<TDbContext, TETag>
        where TProvider : EFCoreProviderConfiguration<TETag>, new()
    {
        var provider = new TProvider();
        await using var databaseFixture = new EFCoreDatabaseFixture<TDbContext>(
            provider.Database,
            "persistence_identity",
            $"{typeof(TDbContext).Name}_{GetTargetFramework()}",
            writeOutput: testOutput.WriteLine);
        await databaseFixture.InitializeAsync();

        var serviceId = $"identity-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<ClusterOptions>().Configure(options =>
        {
            options.ClusterId = $"identity-{Guid.NewGuid():N}";
            options.ServiceId = serviceId;
        });
        services.AddSingleton(databaseFixture.Factory);
        services.AddSingleton(provider.CreateGrainStorageETagConverter());
        services.AddSerializer();
        services.AddSingleton<IGrainStorageSerializer, OrleansGrainStorageSerializer>();
        await using var serviceProvider = services.BuildServiceProvider();
        var storage = EFStorageFactory.Create<TDbContext, TETag>(serviceProvider, StorageName);
        var serializer = serviceProvider.GetRequiredService<IGrainStorageSerializer>();
        var converter = provider.CreateGrainStorageETagConverter();

        const string unspacedIdentifier = "terminal-space-identity";
        const string spacedIdentifier = "terminal-space-identity ";
        var unspacedId = GrainId.Create("persistence-identity", unspacedIdentifier);
        var spacedId = GrainId.Create("persistence-identity", spacedIdentifier);
        Assert.NotEqual(unspacedId, spacedId);
        Assert.NotEqual(unspacedId.Key.ToString(), spacedId.Key.ToString());

        var unspaced = new GrainState<IdentifierState>(NewState("unspaced-first", 101));
        var spaced = new GrainState<IdentifierState>(NewState("spaced-first", 202));
        await storage.WriteStateAsync("profile", unspacedId, unspaced);
        await storage.WriteStateAsync("profile", spacedId, spaced);

        await using (var context = await databaseFixture.Factory.CreateDbContextAsync())
        {
            var inserted = (await context.GrainState.AsNoTracking().ToListAsync())
                .OrderBy(record => record.GrainId.Length)
                .ToList();
            Assert.Equal(2, inserted.Count);
            Assert.Equal(unspacedIdentifier, inserted[0].GrainId);
            Assert.Equal(spacedIdentifier, inserted[1].GrainId);
            var unspacedPersisted = Deserialize(serializer, inserted[0]);
            var spacedPersisted = Deserialize(serializer, inserted[1]);
            Assert.Equal("unspaced-first", unspacedPersisted.Name);
            Assert.Equal(101, unspacedPersisted.Revision);
            Assert.Equal("spaced-first", spacedPersisted.Name);
            Assert.Equal(202, spacedPersisted.Revision);
        }

        var unspacedRead = await Read(storage, unspacedId);
        var spacedRead = await Read(storage, spacedId);
        AssertState("unspaced-first", 101, unspaced.ETag, unspacedRead);
        AssertState("spaced-first", 202, spaced.ETag, spacedRead);

        unspaced.State = NewState("unspaced-second", 303);
        spaced.State = NewState("spaced-second", 404);
        await storage.WriteStateAsync("profile", unspacedId, unspaced);
        await storage.WriteStateAsync("profile", spacedId, spaced);
        var spacedWinnerETag = spaced.ETag;
        var spacedWinnerData = serializer.Serialize(spaced.State).ToArray();

        await storage.ClearStateAsync("profile", unspacedId, unspaced);

        Assert.False(unspaced.RecordExists);
        Assert.Null(unspaced.ETag);
        var spacedAfterOtherClear = await Read(storage, spacedId);
        AssertState("spaced-second", 404, spacedWinnerETag, spacedAfterOtherClear);
        await using (var context = await databaseFixture.Factory.CreateDbContextAsync())
        {
            var survivor = Assert.Single(await context.GrainState.AsNoTracking().ToListAsync());
            Assert.Equal(serviceId, survivor.ServiceId);
            Assert.Equal(spacedIdentifier, survivor.GrainId);
            Assert.Equal(spacedWinnerETag, converter.FromDbETag(survivor.ETag));
            Assert.True(spacedWinnerData.AsSpan().SequenceEqual(Assert.IsType<byte[]>(survivor.Data)));
        }

        await storage.ClearStateAsync("profile", spacedId, spaced);

        Assert.False(spaced.RecordExists);
        Assert.Null(spaced.ETag);
        await using (var context = await databaseFixture.Factory.CreateDbContextAsync())
        {
            Assert.Empty(await context.GrainState.AsNoTracking().ToListAsync());
        }
    }

    private static IdentifierState NewState(string name, int revision) =>
        new() { Name = name, Revision = revision };

    private static IdentifierState Deserialize<TETag>(
        IGrainStorageSerializer serializer,
        GrainStateRecord<TETag> record) =>
        serializer.Deserialize<IdentifierState>(Assert.IsType<byte[]>(record.Data))
        ?? throw new InvalidOperationException("The persisted identifier state was null.");

    private static async Task<GrainState<IdentifierState>> Read(
        IGrainStorage storage,
        GrainId grainId)
    {
        var result = new GrainState<IdentifierState>();
        await storage.ReadStateAsync("profile", grainId, result);
        return result;
    }

    private static void AssertState(
        string expectedName,
        int expectedRevision,
        string? expectedETag,
        GrainState<IdentifierState> actual)
    {
        Assert.True(actual.RecordExists);
        Assert.Equal(expectedETag, actual.ETag);
        Assert.Equal(expectedName, actual.State?.Name);
        Assert.Equal(expectedRevision, actual.State?.Revision);
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

    [GenerateSerializer]
    internal sealed class IdentifierState
    {
        [Id(0)]
        public string? Name { get; set; }

        [Id(1)]
        public int Revision { get; set; }
    }
}
