using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
public class DynamoDBGrainStorageKeyFormatTests : IAsyncLifetime
{
    private const string GrainType = "KeyFormatGrain";
    private const string ClusterServiceId = "cluster-service";

    private readonly TestEnvironmentFixture _fixture;
    private readonly string _tableName = $"KeyFormat{Guid.NewGuid():N}";

    public DynamoDBGrainStorageKeyFormatTests(TestEnvironmentFixture fixture)
    {
        _fixture = fixture;
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        // every test has its own table, since the key format is recorded per table
        if (!AWSTestConstants.IsDynamoDbAvailable)
        {
            return;
        }

        try
        {
            await new DynamoDBStorage(NullLogger<DynamoDBStorage>.Instance, AWSTestConstants.DynamoDbService).DeleTableAsync(_tableName);
        }
        catch (ResourceNotFoundException)
        {
        }
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
    public async Task DynamoDBGrainStorage_MigrateLegacyKeysWithDeleteStateOnClear_ClearingCurrentStateRetiresTheLegacyItem()
    {
        // written on the current key by another silo that does not migrate, so the legacy item is still there
        var grainId = NewGrainId();
        await WriteAsync(await CreateStorage(), grainId, "legacy");
        var storage = await CreateStorage(o => { o.UseClusterServiceId = true; o.MigrateLegacyKeys = true; o.DeleteStateOnClear = true; });
        await WriteAsync(await CreateStorage(o => { o.UseClusterServiceId = true; o.MigrateLegacyKeys = false; }), grainId, "current");

        await storage.ClearStateAsync(GrainType, grainId, await ReadAsync(storage, grainId));

        Assert.False((await ReadAsync(storage, grainId)).RecordExists);
        Assert.False(await RowExists($"_{grainId}"));
    }

    [Fact, TestCategory("Functional")]
    public async Task DynamoDBGrainStorage_MigrateLegacyKeys_ClearingCurrentStateKeepingTheItemRetiresTheLegacyItem()
    {
        var grainId = NewGrainId();
        await WriteAsync(await CreateStorage(), grainId, "legacy");
        var storage = await CreateStorage(o => { o.UseClusterServiceId = true; o.MigrateLegacyKeys = true; });
        await WriteAsync(await CreateStorage(o => { o.UseClusterServiceId = true; o.MigrateLegacyKeys = false; }), grainId, "current");

        await storage.ClearStateAsync(GrainType, grainId, await ReadAsync(storage, grainId));

        Assert.False(await RowExists($"_{grainId}"));
        Assert.True(await RowExists($"{ClusterServiceId}_{grainId}"));
        Assert.False((await ReadAsync(storage, grainId)).RecordExists);
    }

    [Fact, TestCategory("Functional")]
    public async Task DynamoDBGrainStorage_MigrateLegacyKeys_WritingCurrentStateRetiresTheLegacyItem()
    {
        var grainId = NewGrainId();
        await WriteAsync(await CreateStorage(), grainId, "legacy");
        var storage = await CreateStorage(o => { o.UseClusterServiceId = true; o.MigrateLegacyKeys = true; });
        await WriteAsync(await CreateStorage(o => { o.UseClusterServiceId = true; o.MigrateLegacyKeys = false; }), grainId, "current");

        await WriteAsync(storage, grainId, "current again");

        Assert.False(await RowExists($"_{grainId}"));
        Assert.Equal("current again", (await ReadAsync(storage, grainId)).State!.A);
        Assert.Equal("1", (await ReadRow($"{ClusterServiceId}_{grainId}"))!["ETag"].N);
    }

    [Fact, TestCategory("Functional")]
    public async Task DynamoDBGrainStorage_MigrateLegacyKeys_StateReadFromTheCurrentKeyIsNotMigrated()
    {
        // an orphaned legacy item with the same ETag must not stand in for a current item deleted meanwhile
        var grainId = NewGrainId();
        await WriteAsync(await CreateStorage(), grainId, "orphan");
        var storage = await CreateStorage(o => { o.UseClusterServiceId = true; o.MigrateLegacyKeys = true; });
        await WriteAsync(await CreateStorage(o => { o.UseClusterServiceId = true; o.MigrateLegacyKeys = false; }), grainId, "current");

        var state = await ReadAsync(storage, grainId);
        Assert.Equal("current", state.State!.A);
        var other = await CreateStorage(o => { o.UseClusterServiceId = true; o.MigrateLegacyKeys = false; o.DeleteStateOnClear = true; });
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

    [Fact, TestCategory("Functional")]
    public async Task DynamoDBGrainStorage_MigrateLegacyKeysWithDeleteStateOnClear_ClearingUnreadStateLeavesTheLegacyItem()
    {
        // a clear of a state that was never read has no ETag, and leaves whatever is there, as before
        var grainId = NewGrainId();
        await WriteAsync(await CreateStorage(), grainId, "legacy");

        var storage = await CreateStorage(o => { o.UseClusterServiceId = true; o.MigrateLegacyKeys = true; o.DeleteStateOnClear = true; });
        await storage.ClearStateAsync(GrainType, grainId, new GrainState<TestStoreGrainState>(new TestStoreGrainState()));

        Assert.True(await RowExists($"_{grainId}"));
        Assert.Equal("legacy", (await ReadAsync(storage, grainId)).State!.A);
    }

    [Fact, TestCategory("Functional")]
    public async Task DynamoDBGrainStorage_UnknownKeyFormatRecord_FailsToStart()
    {
        await CreateStorage();
        await WriteKeyFormatRecord("SomethingNewer");

        var exception = await Assert.ThrowsAsync<OrleansConfigurationException>(() => CreateStorage());

        Assert.Contains("SomethingNewer", exception.Message);
    }

    [Fact(Timeout = 15_000), TestCategory("Functional")]
    public async Task DynamoDBGrainStorage_KeyFormatRecordWithoutValue_FailsToStart()
    {
        await CreateStorage();
        await WriteKeyFormatRecord(null);

        // a record that is there but says nothing must not be taken for a missing one, whose write would then be retried for ever
        await Assert.ThrowsAsync<OrleansConfigurationException>(() => CreateStorageUntil(TestContext.Current.CancellationToken));
    }

    [Theory, TestCategory("Functional")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DynamoDBGrainStorage_UseClusterServiceIdFalseOverRecordedClusterServiceId_FailsToStart(bool migrating)
    {
        // going back to the empty ServiceId would hide this state, for this silo and every one that follows the record
        var grainId = NewGrainId();
        await WriteAsync(await CreateStorage(o => { o.UseClusterServiceId = true; o.MigrateLegacyKeys = migrating; }), grainId, "current");

        var exception = await Assert.ThrowsAsync<OrleansConfigurationException>(() => CreateStorage(o => o.UseClusterServiceId = false));

        Assert.Contains("UseClusterServiceId is false", exception.Message);
        Assert.Equal(migrating ? "MigratingToClusterServiceId" : "ClusterServiceId", (await ReadRow("__OrleansKeyFormat", "__OrleansKeyFormat"))!["KeyFormat"].S);
        Assert.Equal("current", (await ReadAsync(await CreateStorage(), grainId)).State!.A);
    }

    [Theory, TestCategory("Functional")]
    [InlineData(true)]
    [InlineData(null)]
    public async Task DynamoDBGrainStorage_MigrateLegacyKeysOverRecordedClusterServiceId_FailsToStart(bool? useClusterServiceId)
    {
        // the grain's state under ClusterOptions.ServiceId was deleted while the migration was off, and a migration would
        // read back the legacy item it left behind
        var grainId = NewGrainId();
        await WriteAsync(await CreateStorage(), grainId, "legacy");
        var withoutMigration = await CreateStorage(o => { o.UseClusterServiceId = true; o.DeleteStateOnClear = true; });
        await WriteAsync(withoutMigration, grainId, "current");
        await withoutMigration.ClearStateAsync(GrainType, grainId, await ReadAsync(withoutMigration, grainId));

        var exception = await Assert.ThrowsAsync<OrleansConfigurationException>(
            () => CreateStorage(o => { o.UseClusterServiceId = useClusterServiceId; o.MigrateLegacyKeys = true; }));

        Assert.Contains("MigrateLegacyKeys is true", exception.Message);
        Assert.Equal("ClusterServiceId", (await ReadRow("__OrleansKeyFormat", "__OrleansKeyFormat"))!["KeyFormat"].S);
        Assert.False((await ReadAsync(await CreateStorage(), grainId)).RecordExists);
    }

    [Fact, TestCategory("Functional")]
    public async Task DynamoDBGrainStorage_MigrateLegacyKeysOnceTheRecordIsDeleted_Starts()
    {
        // what the refusal says to do, to migrate anyway
        await CreateStorage(o => o.UseClusterServiceId = true);
        await DeleteKeyFormatRecord();

        await CreateStorage(o => { o.UseClusterServiceId = true; o.MigrateLegacyKeys = true; });

        Assert.Equal("MigratingToClusterServiceId", (await ReadRow("__OrleansKeyFormat", "__OrleansKeyFormat"))!["KeyFormat"].S);
    }

    [Fact, TestCategory("Functional")]
    public async Task DynamoDBGrainStorage_MigrateLegacyKeys_WriteWithAnETagThatIsNotANumberIsInconsistent()
    {
        var grainId = NewGrainId();
        var storage = await CreateStorage(o => { o.UseClusterServiceId = true; o.MigrateLegacyKeys = true; });
        await WriteAsync(storage, grainId, "a");
        await WriteAsync(storage, grainId, "b");

        var state = await ReadAsync(storage, grainId);
        state.ETag = "not a number";

        await Assert.ThrowsAsync<InconsistentStateException>(() => storage.WriteStateAsync(GrainType, grainId, state));
        Assert.Equal("b", (await ReadAsync(storage, grainId)).State!.A);
    }

    [Fact, TestCategory("Functional")]
    public async Task DynamoDBGrainStorage_KeyFormatOptionsWithExplicitServiceId_LogsThatTheyHaveNoEffect()
    {
        var logger = new CapturingLogger();
        await CreateStorage(o => { o.ServiceId = "explicit"; o.MigrateLegacyKeys = true; }, logger: logger);

        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("UseClusterServiceId and MigrateLegacyKeys have no effect"));
    }

    [Fact, TestCategory("Functional")]
    public async Task DynamoDBGrainStorage_MigrateLegacyKeysWithEmptyServiceId_LogsThatItHasNoEffect()
    {
        var logger = new CapturingLogger();
        await CreateStorage(o => { o.UseClusterServiceId = false; o.MigrateLegacyKeys = true; }, logger: logger);

        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("MigrateLegacyKeys has no effect"));
    }

    [Fact, TestCategory("Functional")]
    public async Task DynamoDBGrainStorage_Init_LogsTheKeyFormat()
    {
        var logger = new CapturingLogger();
        await CreateStorage(o => o.UseClusterServiceId = true, logger: logger);

        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Information
            && entry.Message.Contains($"from the ServiceId '{ClusterServiceId}' (key format ClusterServiceId)"));
    }

    private async Task DeleteKeyFormatRecord()
    {
        var storage = new DynamoDBStorage(NullLogger<DynamoDBStorage>.Instance, AWSTestConstants.DynamoDbService);
        await storage.DeleteEntryAsync(_tableName, new Dictionary<string, AttributeValue>
        {
            { "GrainReference", new AttributeValue("__OrleansKeyFormat") },
            { "GrainType", new AttributeValue("__OrleansKeyFormat") }
        });
    }

    private async Task WriteKeyFormatRecord(string? format)
    {
        var storage = new DynamoDBStorage(NullLogger<DynamoDBStorage>.Instance, AWSTestConstants.DynamoDbService);
        var fields = new Dictionary<string, AttributeValue>
        {
            { "GrainReference", new AttributeValue("__OrleansKeyFormat") },
            { "GrainType", new AttributeValue("__OrleansKeyFormat") }
        };
        if (format is not null)
        {
            fields.Add("KeyFormat", new AttributeValue(format));
        }

        await storage.PutEntryAsync(_tableName, fields);
    }

    private static GrainId NewGrainId() => GrainId.Create("keyformat", Guid.NewGuid().ToString("N"));

    private Task<DynamoDBGrainStorage> CreateStorage(Action<DynamoDBStorageOptions>? configure = null, Action<DynamoDBGrainStorage>? beforeInit = null, ILogger<DynamoDBGrainStorage>? logger = null) =>
        CreateStorageUntil(CancellationToken.None, configure, beforeInit, logger);

    private async Task<DynamoDBGrainStorage> CreateStorageUntil(CancellationToken cancellationToken, Action<DynamoDBStorageOptions>? configure = null, Action<DynamoDBGrainStorage>? beforeInit = null, ILogger<DynamoDBGrainStorage>? logger = null)
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

        var storage = ActivatorUtilities.CreateInstance<DynamoDBGrainStorage>(_fixture.Services, "KeyFormatTests", options, logger ?? NullLogger<DynamoDBGrainStorage>.Instance);
        beforeInit?.Invoke(storage);
        await storage.Init(cancellationToken);
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

    private sealed class CapturingLogger : ILogger<DynamoDBGrainStorage>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
