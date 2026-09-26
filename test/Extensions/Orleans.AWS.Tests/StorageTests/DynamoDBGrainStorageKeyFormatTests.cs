using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.AWSUtils.Tests;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Storage;
using TestExtensions;
using UnitTests.Persistence;
using Xunit;

namespace AWSUtils.Tests.StorageTests;

/// <summary>
/// Tests the keys DynamoDB grain storage builds when <see cref="DynamoDBStorageOptions.ServiceId"/> is empty.
/// </summary>
[TestCategory("Persistence"), TestCategory("AWS"), TestCategory("DynamoDb")]
[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestSuite("Functional")]
[TestProvider("DynamoDB")]
[TestArea("Persistence")]
public class DynamoDBGrainStorageKeyFormatTests
{
    private const string GrainType = "KeyFormatGrain";
    private const string ClusterServiceId = "cluster-service";

    private readonly TestEnvironmentFixture _fixture;
    private readonly string _tableName = $"KeyFormat{Guid.NewGuid():N}";

    public DynamoDBGrainStorageKeyFormatTests(TestEnvironmentFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact, TestCategory("Functional")]
    public async Task DynamoDBGrainStorage_EmptyServiceId_KeepsKeysWithoutServiceId()
    {
        var grainId = NewGrainId();
        await WriteAsync(await CreateStorage(), grainId, "a");

        Assert.True(await RowExists($"_{grainId}"));
        Assert.False(await RowExists($"{ClusterServiceId}_{grainId}"));
    }

    [Fact, TestCategory("Functional")]
    public async Task DynamoDBGrainStorage_UseClusterServiceId_BuildsKeysFromClusterServiceId()
    {
        var grainId = NewGrainId();
        await WriteAsync(await CreateStorage(o => o.UseClusterServiceId = true), grainId, "a");

        Assert.True(await RowExists($"{ClusterServiceId}_{grainId}"));
        Assert.False(await RowExists($"_{grainId}"));
    }

    [Fact, TestCategory("Functional")]
    public async Task DynamoDBGrainStorage_ExplicitServiceId_IgnoresClusterServiceId()
    {
        var grainId = NewGrainId();
        await WriteAsync(await CreateStorage(o => { o.ServiceId = "explicit"; o.UseClusterServiceId = true; o.MigrateLegacyKeys = true; }), grainId, "a");

        Assert.True(await RowExists($"explicit_{grainId}"));
    }

    [Fact, TestCategory("Functional")]
    public async Task DynamoDBGrainStorage_UseClusterServiceIdWithoutMigration_DoesNotReadLegacyState()
    {
        var grainId = NewGrainId();
        await WriteAsync(await CreateStorage(), grainId, "legacy");

        var state = await ReadAsync(await CreateStorage(o => o.UseClusterServiceId = true), grainId);

        Assert.False(state.RecordExists);
    }

    [Fact, TestCategory("Functional")]
    public async Task DynamoDBGrainStorage_MigrateLegacyKeys_ReadsLegacyStateAndMovesItOnWrite()
    {
        var grainId = NewGrainId();
        await WriteAsync(await CreateStorage(), grainId, "legacy");

        var storage = await CreateStorage(o => { o.UseClusterServiceId = true; o.MigrateLegacyKeys = true; });
        var state = await ReadAsync(storage, grainId);
        Assert.Equal("legacy", state.State!.A);
        Assert.True(await RowExists($"_{grainId}"));

        state.State!.A = "migrated";
        await storage.WriteStateAsync(GrainType, grainId, state);

        Assert.False(await RowExists($"_{grainId}"));
        Assert.True(await RowExists($"{ClusterServiceId}_{grainId}"));
        Assert.Equal("migrated", (await ReadAsync(storage, grainId)).State!.A);
    }

    [Fact, TestCategory("Functional")]
    public async Task DynamoDBGrainStorage_MigrateLegacyKeys_MovesStateReadByAnotherInstance()
    {
        // nothing is remembered between the read and the write, so a retry or another silo moves the state as well
        var grainId = NewGrainId();
        await WriteAsync(await CreateStorage(), grainId, "legacy");

        var state = await ReadAsync(await CreateStorage(o => { o.UseClusterServiceId = true; o.MigrateLegacyKeys = true; }), grainId);
        state.State!.A = "migrated";
        await (await CreateStorage(o => { o.UseClusterServiceId = true; o.MigrateLegacyKeys = true; })).WriteStateAsync(GrainType, grainId, state);

        Assert.False(await RowExists($"_{grainId}"));
        Assert.True(await RowExists($"{ClusterServiceId}_{grainId}"));
    }

    [Fact, TestCategory("Functional")]
    public async Task DynamoDBGrainStorage_MigrateLegacyKeys_StaleLegacyStateIsInconsistent()
    {
        var grainId = NewGrainId();
        var legacy = await CreateStorage();
        await WriteAsync(legacy, grainId, "legacy");

        var storage = await CreateStorage(o => { o.UseClusterServiceId = true; o.MigrateLegacyKeys = true; });
        var state = await ReadAsync(storage, grainId);
        await WriteAsync(legacy, grainId, "changed meanwhile");

        await Assert.ThrowsAsync<InconsistentStateException>(() => storage.WriteStateAsync(GrainType, grainId, state));
        Assert.True(await RowExists($"_{grainId}"));
        Assert.False(await RowExists($"{ClusterServiceId}_{grainId}"));
    }

    [Fact, TestCategory("Functional")]
    public async Task DynamoDBGrainStorage_MigrateLegacyKeysWithoutTimeToLive_KeepsTheLegacyExpiry()
    {
        var grainId = NewGrainId();
        await WriteAsync(await CreateStorage(o => o.TimeToLive = TimeSpan.FromDays(3)), grainId, "legacy");
        var legacyTtl = (await ReadRow($"_{grainId}"))!["GrainTtl"].N;

        await WriteAsync(await CreateStorage(o => { o.UseClusterServiceId = true; o.MigrateLegacyKeys = true; }), grainId, "migrated");

        Assert.Equal(legacyTtl, (await ReadRow($"{ClusterServiceId}_{grainId}"))!["GrainTtl"].N);
    }

    [Fact, TestCategory("Functional")]
    public async Task DynamoDBGrainStorage_MigrateLegacyKeysWithDeleteStateOnClear_CurrentStateWrittenMeanwhileIsInconsistent()
    {
        var grainId = NewGrainId();
        await WriteAsync(await CreateStorage(), grainId, "legacy");

        var storage = await CreateStorage(o => { o.UseClusterServiceId = true; o.MigrateLegacyKeys = true; o.DeleteStateOnClear = true; });
        var state = await ReadAsync(storage, grainId);

        // another silo migrates the grain and writes it before this clear
        await WriteAsync(await CreateStorage(o => { o.UseClusterServiceId = true; o.MigrateLegacyKeys = true; }), grainId, "current");

        await Assert.ThrowsAsync<InconsistentStateException>(() => storage.ClearStateAsync(GrainType, grainId, state));
        Assert.Equal("current", (await ReadAsync(storage, grainId)).State!.A);
    }

    [Fact, TestCategory("Functional")]
    public async Task DynamoDBGrainStorage_MigrateLegacyKeysWithDeleteStateOnClear_CurrentStateBesideLegacyStateIsInconsistent()
    {
        var grainId = NewGrainId();
        await WriteAsync(await CreateStorage(), grainId, "legacy");

        var storage = await CreateStorage(o => { o.UseClusterServiceId = true; o.MigrateLegacyKeys = true; o.DeleteStateOnClear = true; });
        var state = await ReadAsync(storage, grainId);

        // another silo writes the current key without migrating, which leaves the legacy item in place
        await WriteAsync(await CreateStorage(o => { o.UseClusterServiceId = true; o.MigrateLegacyKeys = false; }), grainId, "current");

        await Assert.ThrowsAsync<InconsistentStateException>(() => storage.ClearStateAsync(GrainType, grainId, state));
        Assert.True(await RowExists($"_{grainId}"));
        Assert.True(await RowExists($"{ClusterServiceId}_{grainId}"));
    }

    [Fact, TestCategory("Functional")]
    public async Task DynamoDBGrainStorage_MigrateLegacyKeys_StateReadFromTheCurrentKeyIsNotMigrated()
    {
        // an orphaned legacy item with the same ETag must not stand in for a current item deleted meanwhile
        var grainId = NewGrainId();
        await WriteAsync(await CreateStorage(), grainId, "orphan");
        await WriteAsync(await CreateStorage(o => o.UseClusterServiceId = true), grainId, "current");

        var storage = await CreateStorage(o => { o.UseClusterServiceId = true; o.MigrateLegacyKeys = true; });
        var state = await ReadAsync(storage, grainId);
        Assert.Equal("current", state.State!.A);
        var other = await CreateStorage(o => { o.UseClusterServiceId = true; o.DeleteStateOnClear = true; });
        await other.ClearStateAsync(GrainType, grainId, await ReadAsync(other, grainId));

        await Assert.ThrowsAsync<InconsistentStateException>(() => storage.WriteStateAsync(GrainType, grainId, state));
        Assert.True(await RowExists($"_{grainId}"));
        Assert.False(await RowExists($"{ClusterServiceId}_{grainId}"));
    }

    [Fact, TestCategory("Functional")]
    public async Task DynamoDBGrainStorage_MigrateLegacyKeys_ClearMovesClearedState()
    {
        var grainId = NewGrainId();
        await WriteAsync(await CreateStorage(), grainId, "legacy");

        var storage = await CreateStorage(o => { o.UseClusterServiceId = true; o.MigrateLegacyKeys = true; });
        var state = await ReadAsync(storage, grainId);
        await storage.ClearStateAsync(GrainType, grainId, state);

        Assert.False(await RowExists($"_{grainId}"));
        Assert.True(await RowExists($"{ClusterServiceId}_{grainId}"));
        Assert.False((await ReadAsync(storage, grainId)).RecordExists);
    }

    [Fact, TestCategory("Functional")]
    public async Task DynamoDBGrainStorage_MigrateLegacyKeysWithDeleteStateOnClear_DeletesLegacyState()
    {
        var grainId = NewGrainId();
        await WriteAsync(await CreateStorage(), grainId, "legacy");

        var storage = await CreateStorage(o => { o.UseClusterServiceId = true; o.MigrateLegacyKeys = true; o.DeleteStateOnClear = true; });
        var state = await ReadAsync(storage, grainId);
        await storage.ClearStateAsync(GrainType, grainId, state);

        Assert.False(await RowExists($"_{grainId}"));
        Assert.False(await RowExists($"{ClusterServiceId}_{grainId}"));
    }

    [Fact, TestCategory("Functional")]
    public async Task DynamoDBGrainStorage_RecordedMigration_GoesOnWithoutOptions()
    {
        var migrated = NewGrainId();
        var pending = NewGrainId();
        var legacy = await CreateStorage();
        await WriteAsync(legacy, migrated, "a");
        await WriteAsync(legacy, pending, "b");
        await WriteAsync(await CreateStorage(o => { o.UseClusterServiceId = true; o.MigrateLegacyKeys = true; }), migrated, "a2");

        var storage = await CreateStorage();

        Assert.Equal("a2", (await ReadAsync(storage, migrated)).State!.A);
        Assert.Equal("b", (await ReadAsync(storage, pending)).State!.A);
    }

    [Fact, TestCategory("Functional")]
    public async Task DynamoDBGrainStorage_RecordedClusterServiceId_IsKeptWithoutOptions()
    {
        var grainId = NewGrainId();
        await WriteAsync(await CreateStorage(o => o.UseClusterServiceId = true), grainId, "a");

        Assert.Equal("a", (await ReadAsync(await CreateStorage(), grainId)).State!.A);
    }

    [Fact, TestCategory("Functional")]
    public async Task DynamoDBGrainStorage_KeyFormatRecordedByAnotherSiloMeanwhile_IsFollowed()
    {
        // this silo reads no record and would keep the empty ServiceId, but another one records a migration first
        var interleaved = false;
        var storage = await CreateStorage(beforeInit: s => s.BeforeKeyFormatWriteForTesting = async () =>
        {
            if (!interleaved)
            {
                interleaved = true;
                await CreateStorage(o => { o.UseClusterServiceId = true; o.MigrateLegacyKeys = true; });
            }
        });

        var grainId = NewGrainId();
        await WriteAsync(storage, grainId, "a");

        Assert.True(interleaved);
        Assert.True(await RowExists($"{ClusterServiceId}_{grainId}"));
        Assert.Equal("MigratingToClusterServiceId", (await ReadRow("__OrleansKeyFormat", "__OrleansKeyFormat"))!["KeyFormat"].S);
    }

    private static GrainId NewGrainId() => GrainId.Create("keyformat", Guid.NewGuid().ToString("N"));

    private async Task<DynamoDBGrainStorage> CreateStorage(Action<DynamoDBStorageOptions>? configure = null, Action<DynamoDBGrainStorage>? beforeInit = null)
    {
        if (!AWSTestConstants.IsDynamoDbAvailable)
        {
            throw Xunit.Sdk.SkipException.ForSkip("Unable to connect to AWS DynamoDB simulator");
        }

        var options = new DynamoDBStorageOptions
        {
            Service = AWSTestConstants.DynamoDbService,
            TableName = _tableName,
            ClusterServiceId = ClusterServiceId,
            GrainStorageSerializer = new OrleansGrainStorageSerializer(_fixture.Services.GetRequiredService<Serializer>()),
        };
        configure?.Invoke(options);

        var storage = ActivatorUtilities.CreateInstance<DynamoDBGrainStorage>(_fixture.Services, "KeyFormatTests", options, NullLogger<DynamoDBGrainStorage>.Instance);
        beforeInit?.Invoke(storage);
        await storage.Init(CancellationToken.None);
        return storage;
    }

    private static async Task WriteAsync(DynamoDBGrainStorage storage, GrainId grainId, string value)
    {
        var state = await ReadAsync(storage, grainId);
        state.State!.A = value;
        await storage.WriteStateAsync(GrainType, grainId, state);
    }

    private static async Task<GrainState<TestStoreGrainState>> ReadAsync(DynamoDBGrainStorage storage, GrainId grainId)
    {
        var state = new GrainState<TestStoreGrainState>(new TestStoreGrainState());
        await storage.ReadStateAsync(GrainType, grainId, state);
        return state;
    }

    private async Task<bool> RowExists(string partitionKey) => await ReadRow(partitionKey) is not null;

    private async Task<Dictionary<string, AttributeValue>?> ReadRow(string partitionKey, string rowKey = GrainType)
    {
        var storage = new DynamoDBStorage(NullLogger<DynamoDBStorage>.Instance, AWSTestConstants.DynamoDbService);
        var row = await storage.ReadSingleEntryAsync(_tableName,
            new Dictionary<string, AttributeValue>
            {
                { "GrainReference", new AttributeValue(partitionKey) },
                { "GrainType", new AttributeValue(rowKey) }
            },
            fields => fields);
        return row;
    }
}
