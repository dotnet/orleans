using System.Collections.Immutable;
using System.Data;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Orleans.Runtime;
using Orleans.Tests.SqlUtils;

namespace Tester.AdoNet;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Clustering")]
public class DbStoredQueriesMembershipMetadataTests
{
    private static readonly string[] V2QueryKeyNames =
    [
        "InsertMembershipV2Key",
        "UpdateMembershipV2Key",
        "MembershipReadRowV2Key",
        "MembershipReadAllV2Key"
    ];

    [Fact]
    public void LegacyOnlyBundle_SelectsLegacyQueries()
    {
        var queries = CreateLegacyQueries();
        var subject = new DbStoredQueries(queries);

        Assert.False(subject.SupportsMembershipMetadata());
        Assert.Equal("legacy:InsertMembershipKey", subject.InsertMembershipKey);
        Assert.Equal("legacy:UpdateMembershipKey", subject.UpdateMembershipKey);
        Assert.Equal("legacy:MembershipReadRowKey", subject.MembershipReadRowKey);
        Assert.Equal("legacy:MembershipReadAllKey", subject.MembershipReadAllKey);
    }

    [Fact]
    public void CompleteV2Bundle_SelectsV2Queries()
    {
        var queries = CreateLegacyQueries();
        AddV2Queries(queries);

        var subject = new DbStoredQueries(queries);

        Assert.True(subject.SupportsMembershipMetadata());
        Assert.Equal("v2:InsertMembershipV2Key", subject.InsertMembershipKey);
        Assert.Equal("v2:UpdateMembershipV2Key", subject.UpdateMembershipKey);
        Assert.Equal("v2:MembershipReadRowV2Key", subject.MembershipReadRowKey);
        Assert.Equal("v2:MembershipReadAllV2Key", subject.MembershipReadAllKey);
    }

    [Fact]
    public void LegacySelection_RemainsCachedWhenV2QueriesAppear()
    {
        var queries = CreateLegacyQueries();
        var subject = new DbStoredQueries(queries);

        AddV2Queries(queries);

        Assert.False(subject.SupportsMembershipMetadata());
        Assert.Equal("legacy:InsertMembershipKey", subject.InsertMembershipKey);
        Assert.Equal("legacy:UpdateMembershipKey", subject.UpdateMembershipKey);
        Assert.Equal("legacy:MembershipReadRowKey", subject.MembershipReadRowKey);
        Assert.Equal("legacy:MembershipReadAllKey", subject.MembershipReadAllKey);
    }

    [Fact]
    public void NewClientInitializedBeforeMigration_UsesV2OnlyAfterReinitialization()
    {
        var queries = CreateLegacyQueries();
        var initializedBeforeMigration = new DbStoredQueries(queries);

        AddV2Queries(queries);
        var initializedAfterMigration = new DbStoredQueries(queries);

        Assert.False(initializedBeforeMigration.SupportsMembershipMetadata());
        Assert.Equal("legacy:InsertMembershipKey", initializedBeforeMigration.InsertMembershipKey);
        Assert.True(initializedAfterMigration.SupportsMembershipMetadata());
        Assert.Equal("v2:InsertMembershipV2Key", initializedAfterMigration.InsertMembershipKey);
    }

    [Fact]
    public void OldAndNewClientsInitializedAfterMigrationSelectIndependentContracts()
    {
        var queries = CreateLegacyQueries();
        AddV2Queries(queries);

        var oldClientInsert = queries["InsertMembershipKey"];
        var oldClientUpdate = queries["UpdateMembershipKey"];
        var newClient = new DbStoredQueries(queries);

        Assert.Equal("legacy:InsertMembershipKey", oldClientInsert);
        Assert.Equal("legacy:UpdateMembershipKey", oldClientUpdate);
        Assert.Equal("v2:InsertMembershipV2Key", newClient.InsertMembershipKey);
        Assert.Equal("v2:UpdateMembershipV2Key", newClient.UpdateMembershipKey);
    }

    [Fact]
    public async Task OldBeforeNewAfterInitialization_WritesWithIndependentContracts()
    {
        var queries = CreateLegacyQueries();
        var oldStorage = new RecordingRelationalStorage();
        var oldClient = CreateRelationalQueries(oldStorage, new DbStoredQueries(queries));

        AddV2Queries(queries);
        var newStorage = new RecordingRelationalStorage();
        var newClient = CreateRelationalQueries(newStorage, new DbStoredQueries(queries));
        var entry = CreateMembershipEntry();

        Assert.True(await oldClient.InsertMembershipRowAsync("cluster", entry, "0"));
        Assert.Equal("legacy:InsertMembershipKey", oldStorage.LastQuery);
        Assert.DoesNotContain("MetadataJson", oldStorage.LastParameterNames);

        Assert.True(await newClient.InsertMembershipRowAsync("cluster", entry, "0"));
        Assert.Equal("v2:InsertMembershipV2Key", newStorage.LastQuery);
        Assert.Contains("MetadataJson", newStorage.LastParameterNames);
    }

    [Fact]
    public async Task NewBeforeOldAfterInitialization_WritesRemainCompatible()
    {
        var queries = CreateLegacyQueries();
        var newBeforeMigrationStorage = new RecordingRelationalStorage();
        var newBeforeMigration = CreateRelationalQueries(
            newBeforeMigrationStorage,
            new DbStoredQueries(queries));

        AddV2Queries(queries);
        var oldAfterMigrationStorage = new RecordingRelationalStorage();
        var oldAfterMigration = CreateRelationalQueries(
            oldAfterMigrationStorage,
            new DbStoredQueries(CreateLegacyView(queries)));
        var newAfterRestartStorage = new RecordingRelationalStorage();
        var newAfterRestart = CreateRelationalQueries(
            newAfterRestartStorage,
            new DbStoredQueries(queries));
        var entry = CreateMembershipEntry();

        Assert.True(await newBeforeMigration.UpdateMembershipRowAsync("cluster", entry, "0"));
        Assert.Equal("legacy:UpdateMembershipKey", newBeforeMigrationStorage.LastQuery);
        Assert.DoesNotContain("MetadataJson", newBeforeMigrationStorage.LastParameterNames);

        Assert.True(await oldAfterMigration.UpdateMembershipRowAsync("cluster", entry, "0"));
        Assert.Equal("legacy:UpdateMembershipKey", oldAfterMigrationStorage.LastQuery);
        Assert.DoesNotContain("MetadataJson", oldAfterMigrationStorage.LastParameterNames);

        Assert.True(await newAfterRestart.UpdateMembershipRowAsync("cluster", entry, "0"));
        Assert.Equal("v2:UpdateMembershipV2Key", newAfterRestartStorage.LastQuery);
        Assert.Contains("MetadataJson", newAfterRestartStorage.LastParameterNames);
    }

    [Theory]
    [MemberData(nameof(V2QueryKeys))]
    public void PartialV2Bundle_IsRejected(string missingKey)
    {
        var queries = CreateLegacyQueries();
        AddV2Queries(queries);
        queries.Remove(missingKey);

        var exception = Assert.Throws<ArgumentException>(() => new DbStoredQueries(queries));

        Assert.Contains(missingKey, exception.Message);
    }

    [Fact]
    public void V2Selection_IsIndependentOfSqlText()
    {
        var legacyQueries = CreateLegacyQueries();
        legacyQueries["InsertMembershipKey"] = "legacy MetadataJson";
        legacyQueries["UpdateMembershipKey"] = "legacy MetadataJson";
        legacyQueries["MembershipReadRowKey"] = "legacy MetadataJson";
        legacyQueries["MembershipReadAllKey"] = "legacy MetadataJson";

        var legacySubject = new DbStoredQueries(legacyQueries);

        Assert.False(legacySubject.SupportsMembershipMetadata());

        AddV2Queries(legacyQueries, "metadata-free");
        var v2Subject = new DbStoredQueries(legacyQueries);

        Assert.True(v2Subject.SupportsMembershipMetadata());
        Assert.Equal("metadata-free", v2Subject.InsertMembershipKey);
    }

    public static TheoryData<string> V2QueryKeys => new(
        "InsertMembershipV2Key",
        "UpdateMembershipV2Key",
        "MembershipReadRowV2Key",
        "MembershipReadAllV2Key");

    private static Dictionary<string, string> CreateLegacyQueries()
        => typeof(DbStoredQueries)
            .GetProperties(BindingFlags.Instance | BindingFlags.NonPublic)
            .ToDictionary(property => property.Name, property => $"legacy:{property.Name}");

    private static void AddV2Queries(Dictionary<string, string> queries, string? value = null)
    {
        foreach (var key in V2QueryKeyNames)
        {
            queries.Add(key, value ?? $"v2:{key}");
        }
    }

    private static Dictionary<string, string> CreateLegacyView(Dictionary<string, string> queries)
        => queries
            .Where(entry => !V2QueryKeyNames.Contains(entry.Key, StringComparer.Ordinal))
            .ToDictionary(entry => entry.Key, entry => entry.Value);

    private static RelationalOrleansQueries CreateRelationalQueries(
        IRelationalStorage storage,
        DbStoredQueries queries)
    {
        var constructor = typeof(RelationalOrleansQueries).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(IRelationalStorage), typeof(DbStoredQueries)],
            modifiers: null);
        Assert.NotNull(constructor);
        return (RelationalOrleansQueries)constructor.Invoke([storage, queries]);
    }

    private static MembershipEntry CreateMembershipEntry()
        => new()
        {
            SiloAddress = SiloAddress.FromParsableString("127.0.0.1:11111@1"),
            SiloName = "silo",
            HostName = "localhost",
            Status = SiloStatus.Active,
            StartTime = DateTime.UtcNow,
            IAmAliveTime = DateTime.UtcNow,
            Metadata = new Dictionary<string, string> { ["region"] = "west" }.ToImmutableDictionary()
        };

    private sealed class RecordingRelationalStorage : IRelationalStorage
    {
        public string InvariantName => "Test";
        public string ConnectionString => string.Empty;
        public string LastQuery { get; private set; } = string.Empty;
        public string[] LastParameterNames { get; private set; } = [];

        public Task<int> ExecuteAsync(
            string query,
            Action<IDbCommand>? parameterProvider,
            CommandBehavior commandBehavior = CommandBehavior.Default,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IEnumerable<TResult>> ReadAsync<TResult>(
            string query,
            Action<IDbCommand>? parameterProvider,
            Func<IDataRecord, int, CancellationToken, Task<TResult>> selector,
            CommandBehavior commandBehavior = CommandBehavior.Default,
            CancellationToken cancellationToken = default)
        {
            using var command = new SqliteCommand();
            parameterProvider?.Invoke(command);
            this.LastQuery = query;
            this.LastParameterNames = command.Parameters
                .Cast<SqliteParameter>()
                .Select(parameter => parameter.ParameterName)
                .ToArray();

            Assert.Equal(typeof(bool), typeof(TResult));
            return Task.FromResult<IEnumerable<TResult>>([(TResult)(object)true]);
        }
    }
}
