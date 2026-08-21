using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.EntityFrameworkCore.Tests.Infrastructure;
using Orleans.Persistence.EntityFrameworkCore.Data;
using Orleans.Runtime;
using Orleans.Storage;
using UnitTests.Persistence;

namespace Orleans.EntityFrameworkCore.Tests.Persistence;

public abstract class EFCorePersistenceProviderTestsBase<TDbContext, TETag, TProvider> : IAsyncLifetime
    where TDbContext : GrainStateDbContext<TDbContext, TETag>
    where TProvider : EFCoreProviderConfiguration<TETag>, new()
{
    private const string StorageName = "PersistenceMatrix";
    private readonly string _serviceId = $"service-{Guid.NewGuid():N}";
    private readonly ITestOutputHelper _testOutput;
    private readonly RecordingLoggerProvider _loggerProvider = new();
    private EFCoreDatabaseFixture<TDbContext>? _databaseFixture;
    private ServiceProvider? _services;
    private EFGrainStorage<TDbContext, TETag>? _storage;

    protected EFCorePersistenceProviderTestsBase(ITestOutputHelper testOutput)
    {
        _testOutput = testOutput;
    }

    public async ValueTask InitializeAsync()
    {
        var provider = new TProvider();
        _databaseFixture = new EFCoreDatabaseFixture<TDbContext>(
            provider.Database,
            "persistence_direct",
            $"{GetType().Name}_{GetTargetFramework()}",
            writeOutput: message => _testOutput.WriteLine(message));
        await _databaseFixture.InitializeAsync();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(_loggerProvider));
        services.AddOptions<ClusterOptions>().Configure(options =>
        {
            options.ClusterId = $"cluster-{Guid.NewGuid():N}";
            options.ServiceId = _serviceId;
        });
        services.AddSingleton(_databaseFixture.Factory);
        services.AddSingleton(provider.CreateGrainStorageETagConverter());
        services.AddSingleton(new ConstructorDependency("resolved-from-di"));
        _services = services.BuildServiceProvider();

        _storage = EFStorageFactory.Create<TDbContext, TETag>(_services, StorageName);
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        if (_databaseFixture is not null)
        {
            await _databaseFixture.DisposeAsync();
        }
    }

    [Fact, TestSuite(EFCoreTestCategories.Functional)]
    public async Task ReadMissingState_UsesDependencyInjectionAndMarksRecordMissing()
    {
        var state = new GrainState<DependencyConstructedState> { ETag = "stale-caller-value" };

        await Storage.ReadStateAsync(
            "missing-state",
            GrainId.Create("persistence-matrix", Guid.NewGuid().ToString("N")),
            state);

        Assert.False(state.RecordExists);
        Assert.Null(state.ETag);
        Assert.Equal("resolved-from-di", Assert.IsType<DependencyConstructedState>(state.State).Value);
        Assert.Empty(await ReadAllRecords());
    }

    [Theory, TestSuite(EFCoreTestCategories.Functional)]
    [InlineData(null)]
    [InlineData(15 * 32 * 1024 - 256)]
    [InlineData(15 * 64 * 1024 - 256)]
    public async Task WriteRead_NewStatePersistsPayloadAndReturnsProviderETag(int? payloadLength)
    {
        var grainId = NewGrainId("write-read");
        var written = CreateState("inserted", payloadLength);

        await Storage.WriteStateAsync("profile", grainId, written);

        Assert.True(written.RecordExists);
        AssertProviderETag(written.ETag);
        var read = await Read("profile", grainId);
        AssertState(written, read);
        Assert.Equal(written.ETag, read.ETag);
        var record = Assert.Single(await ReadAllRecords());
        Assert.Equal(_serviceId, record.ServiceId);
        Assert.Equal("profile", record.StateType);
        Assert.Equal(grainId.Type.ToString(), record.GrainType);
        Assert.Equal(grainId.Key.ToString(), record.GrainId);
    }

    [Fact, TestSuite(EFCoreTestCategories.Functional)]
    public async Task ValidUpdate_ChangesDataAndRotatesETag()
    {
        var grainId = NewGrainId("valid-update");
        var state = CreateState("before");
        await Storage.WriteStateAsync("profile", grainId, state);
        var insertedETag = state.ETag;

        state.State = NewValue("winner", 702, 9_003);
        await Storage.WriteStateAsync("profile", grainId, state);

        Assert.NotEqual(insertedETag, state.ETag);
        AssertProviderETag(state.ETag);
        var read = await Read("profile", grainId);
        AssertState(state, read);
        Assert.Equal(state.ETag, read.ETag);
        Assert.Single(await ReadAllRecords());
    }

    [Fact, TestSuite(EFCoreTestCategories.Functional)]
    public async Task WildcardWrite_OverwritesWinnerAndReturnsActualRotatedETag()
    {
        var grainId = NewGrainId("wildcard");
        var original = CreateState("original");
        await Storage.WriteStateAsync("profile", grainId, original);
        var originalETag = original.ETag;
        var replacement = CreateState("wildcard-winner");
        replacement.ETag = "*";

        await Storage.WriteStateAsync("profile", grainId, replacement);

        Assert.True(replacement.RecordExists);
        Assert.NotEqual("*", replacement.ETag);
        Assert.NotEqual(originalETag, replacement.ETag);
        AssertProviderETag(replacement.ETag);
        var read = await Read("profile", grainId);
        AssertState(replacement, read);
        Assert.Equal(replacement.ETag, read.ETag);
    }

    [Fact, TestSuite(EFCoreTestCategories.Functional)]
    public async Task DuplicateInsertWithoutETag_FailsAndPreservesOriginal()
    {
        var grainId = NewGrainId("duplicate");
        var original = CreateState("original");
        await Storage.WriteStateAsync("profile", grainId, original);
        var duplicate = CreateState("duplicate");

        await Assert.ThrowsAsync<DbUpdateException>(
            () => Storage.WriteStateAsync("profile", grainId, duplicate));

        Assert.False(duplicate.RecordExists);
        Assert.Null(duplicate.ETag);
        var read = await Read("profile", grainId);
        AssertState(original, read);
        Assert.Equal(original.ETag, read.ETag);
        Assert.Single(await ReadAllRecords());
    }

    [Fact, TestSuite(EFCoreTestCategories.Functional)]
    public async Task StaleWrite_ThrowsInconsistentStateException_AndPreservesWinner()
    {
        var grainId = NewGrainId("stale-write");
        var winner = CreateState("initial");
        await Storage.WriteStateAsync("profile", grainId, winner);
        var stale = Clone(winner);
        var staleETag = stale.ETag;

        winner.State = NewValue("winner", 808, 10_004);
        await Storage.WriteStateAsync("profile", grainId, winner);
        stale.State = NewValue("stale-loser", 909, 11_005);

        var exception = await Assert.ThrowsAsync<InconsistentStateException>(
            () => Storage.WriteStateAsync("profile", grainId, stale));

        Assert.Equal(staleETag, exception.CurrentEtag);
        Assert.NotEqual(staleETag, winner.ETag);
        AssertGuidETagRotatedWhenApplicable(staleETag, winner.ETag);
        var read = await Read("profile", grainId);
        AssertState(winner, read);
        Assert.Equal(winner.ETag, read.ETag);
        Assert.Single(await ReadAllRecords());
    }

    [Fact, TestSuite(EFCoreTestCategories.Functional)]
    public async Task StaleClear_ThrowsInconsistentStateException_AndPreservesRow()
    {
        var grainId = NewGrainId("stale-clear");
        var winner = CreateState("initial");
        await Storage.WriteStateAsync("profile", grainId, winner);
        var stale = Clone(winner);
        var staleETag = stale.ETag;

        winner.State = NewValue("winner", 1_010, 12_006);
        await Storage.WriteStateAsync("profile", grainId, winner);

        var exception = await Assert.ThrowsAsync<InconsistentStateException>(
            () => Storage.ClearStateAsync("profile", grainId, stale));

        Assert.Equal(staleETag, exception.CurrentEtag);
        AssertGuidETagRotatedWhenApplicable(staleETag, winner.ETag);
        var read = await Read("profile", grainId);
        Assert.True(read.RecordExists);
        AssertState(winner, read);
        Assert.Equal(winner.ETag, read.ETag);
        Assert.Single(await ReadAllRecords());
    }

    [Fact, TestSuite(EFCoreTestCategories.Functional)]
    public async Task ClearExistingState_RemovesRecordAndResetsCaller()
    {
        var grainId = NewGrainId("clear");
        var state = CreateState("clear-me");
        await Storage.WriteStateAsync("profile", grainId, state);

        await Storage.ClearStateAsync("profile", grainId, state);

        Assert.False(state.RecordExists);
        Assert.Null(state.ETag);
        var reset = Assert.IsType<PersistenceState>(state.State);
        Assert.Null(reset.Name);
        Assert.Equal(0, reset.Revision);
        Assert.Equal(0, reset.Checksum);
        var read = await Read("profile", grainId);
        Assert.False(read.RecordExists);
        Assert.Empty(await ReadAllRecords());
    }

    [Fact, TestSuite(EFCoreTestCategories.Functional)]
    public async Task ClearMissingState_IsIdempotentButClaimedExistingRowFailsConcurrency()
    {
        var grainId = NewGrainId("clear-missing");
        var missing = CreateState("not-written");
        missing.ETag = "ignored-because-record-is-missing";

        await Storage.ClearStateAsync("profile", grainId, missing);

        Assert.False(missing.RecordExists);
        Assert.Null(missing.ETag);
        Assert.Null(Assert.IsType<PersistenceState>(missing.State).Name);

        var claimed = CreateState("claimed");
        claimed.RecordExists = true;
        claimed.ETag = CreateUnknownETag();
        await Assert.ThrowsAsync<InconsistentStateException>(
            () => Storage.ClearStateAsync("profile", grainId, claimed));
        Assert.Empty(await ReadAllRecords());
    }

    [Fact, TestSuite(EFCoreTestCategories.Functional)]
    public async Task CompleteFourColumnKey_IsolatesServiceTypeStateAndGrainId()
    {
        var primaryId = NewGrainId("primary");
        var otherId = NewGrainId("other-id");
        var otherTypeId = GrainId.Create("other-grain-type", primaryId.Key.ToString());
        var primary = CreateState("primary");
        var otherStateName = CreateState("other-state-name");
        var otherGrainType = CreateState("other-grain-type");
        var otherGrainId = CreateState("other-grain-id");
        var otherService = CreateState("other-service");
        var secondStorage = CreateStorage($"other-service-{Guid.NewGuid():N}");

        await Storage.WriteStateAsync("profile", primaryId, primary);
        await Storage.WriteStateAsync("preferences", primaryId, otherStateName);
        await Storage.WriteStateAsync("profile", otherTypeId, otherGrainType);
        await Storage.WriteStateAsync("profile", otherId, otherGrainId);
        await secondStorage.WriteStateAsync("profile", primaryId, otherService);

        AssertState(primary, await Read("profile", primaryId));
        AssertState(otherStateName, await Read("preferences", primaryId));
        AssertState(otherGrainType, await Read("profile", otherTypeId));
        AssertState(otherGrainId, await Read("profile", otherId));
        AssertState(otherService, await Read(secondStorage, "profile", primaryId));

        var records = await ReadAllRecords();
        Assert.Equal(5, records.Count);
        Assert.Equal(2, records.Select(record => record.ServiceId).Distinct().Count());
        Assert.Equal(2, records.Select(record => record.StateType).Distinct().Count());
        Assert.Equal(2, records.Select(record => record.GrainType).Distinct().Count());
        Assert.Equal(2, records.Select(record => record.GrainId).Distinct().Count());
    }

    [Fact, TestSuite(EFCoreTestCategories.Functional)]
    public void Participate_LogsNamedStorageInitializationAndFactoryCreatesStorage()
    {
        Storage.Participate(new NoopSiloLifecycle());

        var log = Assert.Single(_loggerProvider.Messages);
        Assert.Equal(LogLevel.Information, log.Level);
        Assert.Contains(StorageName, log.Message, StringComparison.Ordinal);
        Assert.Contains("initialized", log.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<EFGrainStorage<TDbContext, TETag>>(Storage);
    }

    private EFGrainStorage<TDbContext, TETag> Storage =>
        _storage ?? throw new InvalidOperationException("The storage has not been initialized.");

    private IDbContextFactory<TDbContext> Factory =>
        _databaseFixture?.Factory ?? throw new InvalidOperationException("The database has not been initialized.");

    private EFGrainStorage<TDbContext, TETag> CreateStorage(string serviceId)
    {
        var loggerFactory = _services!.GetRequiredService<ILoggerFactory>();
        return new EFGrainStorage<TDbContext, TETag>(
            StorageName,
            loggerFactory,
            Options.Create(new ClusterOptions
            {
                ClusterId = $"cluster-{Guid.NewGuid():N}",
                ServiceId = serviceId
            }),
            Factory,
            new TProvider().CreateGrainStorageETagConverter(),
            _services ?? throw new InvalidOperationException("The services have not been initialized."));
    }

    private async Task<GrainState<PersistenceState>> Read(string stateName, GrainId grainId) =>
        await Read(Storage, stateName, grainId);

    private static async Task<GrainState<PersistenceState>> Read(
        IGrainStorage storage,
        string stateName,
        GrainId grainId)
    {
        var state = new GrainState<PersistenceState>();
        await storage.ReadStateAsync(stateName, grainId, state);
        return state;
    }

    private async Task<List<GrainStateRecord<TETag>>> ReadAllRecords()
    {
        await using var context = await Factory.CreateDbContextAsync();
        return await context.GrainState.AsNoTracking().ToListAsync();
    }

    private static GrainId NewGrainId(string suffix) =>
        GrainId.Create("persistence-matrix", $"{suffix}-{Guid.NewGuid():N}");

    private static GrainState<PersistenceState> CreateState(string name, int? payloadLength = null) =>
        new(NewValue(
            payloadLength is null ? name : $"{name}:{new string('7', payloadLength.Value)}",
            101,
            8_002));

    private static PersistenceState NewValue(string? name, int revision, long checksum) =>
        new()
        {
            Name = name,
            Revision = revision,
            Checksum = checksum
        };

    private static GrainState<PersistenceState> Clone(GrainState<PersistenceState> source)
    {
        var sourceState = source.State!;
        return new(NewValue(sourceState.Name, sourceState.Revision, sourceState.Checksum))
        {
            ETag = source.ETag,
            RecordExists = source.RecordExists
        };
    }

    private static void AssertState(
        GrainState<PersistenceState> expected,
        GrainState<PersistenceState> actual)
    {
        Assert.True(actual.RecordExists);
        var expectedState = expected.State!;
        var actualState = actual.State!;
        Assert.Equal(expectedState.Name, actualState.Name);
        Assert.Equal(expectedState.Revision, actualState.Revision);
        Assert.Equal(expectedState.Checksum, actualState.Checksum);
    }

    private static void AssertProviderETag(string? etag)
    {
        Assert.False(string.IsNullOrWhiteSpace(etag));
        Assert.NotEqual("*", etag);
        if (typeof(TETag) == typeof(Guid))
        {
            Assert.True(Guid.TryParseExact(etag, "D", out var value));
            Assert.NotEqual(Guid.Empty, value);
        }
    }

    private static void AssertGuidETagRotatedWhenApplicable(string? staleETag, string? winnerETag)
    {
        if (typeof(TETag) != typeof(Guid))
        {
            return;
        }

        Assert.True(Guid.TryParseExact(staleETag, "D", out var staleGuid));
        Assert.True(Guid.TryParseExact(winnerETag, "D", out var winnerGuid));
        Assert.NotEqual(Guid.Empty, staleGuid);
        Assert.NotEqual(Guid.Empty, winnerGuid);
        Assert.NotEqual(staleGuid, winnerGuid);
    }

    private static string CreateUnknownETag() =>
        typeof(TETag) == typeof(Guid)
            ? Guid.NewGuid().ToString("D")
            : Convert.ToBase64String([1, 2, 3, 4, 5, 6, 7, 8]);

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

    public sealed class PersistenceState
    {
        public string? Name { get; set; }

        public int Revision { get; set; }

        public long Checksum { get; set; }
    }

    public sealed class DependencyConstructedState
    {
        public DependencyConstructedState(ConstructorDependency dependency)
        {
            Value = dependency.Value;
        }

        public string Value { get; }
    }

    public sealed record ConstructorDependency(string Value);

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public List<LogEntry> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(Messages);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(List<LogEntry> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                messages.Add(new LogEntry(logLevel, formatter(state, exception)));
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class NoopSiloLifecycle : ISiloLifecycle
    {
        public int HighestCompletedStage => 0;

        public int LowestStoppedStage => 0;

        public IDisposable Subscribe(string observerName, int stage, ILifecycleObserver observer) =>
            NoopDisposable.Instance;
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
