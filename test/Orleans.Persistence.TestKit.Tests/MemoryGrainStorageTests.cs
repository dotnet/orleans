using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;
using Orleans.Persistence.TestKit;
using Orleans.Runtime;
using Orleans.Serialization.Serializers;
using Orleans.Storage;
using Xunit;

namespace Orleans.Persistence.Memory.Tests;

/// <summary>
/// Example test fixture showing how to configure MemoryGrainStorage for testing.
/// </summary>
public class MemoryGrainStorageTestFixture : GrainStorageTestFixture, IAsyncLifetime
{
    protected override string StorageProviderName => "MemoryStore";

    protected override void ConfigureSilo(ISiloBuilder siloBuilder)
    {
        siloBuilder.AddMemoryGrainStorage("MemoryStore");
    }

    public IGrainStorage CreateStorageWithLatency()
    {
        var services = Cluster.Silos[0].ServiceProvider;
        return new MemoryGrainStorageWithLatency(
            "MemoryStoreWithLatency",
            new MemoryStorageWithLatencyOptions { Latency = TimeSpan.Zero },
            services.GetRequiredService<ILoggerFactory>(),
            services.GetRequiredService<IGrainFactory>(),
            services.GetRequiredService<IActivatorProvider>(),
            services.GetRequiredService<IGrainStorageSerializer>());
    }
}

/// <summary>
/// Example tests demonstrating how to use the Orleans.Persistence.TestKit
/// to test the MemoryGrainStorage provider.
/// </summary>
[TestCategory("Persistence"), TestCategory("MemoryStore")]
public class MemoryGrainStorageTests : GrainStorageTestRunner, IClassFixture<MemoryGrainStorageTestFixture>
{
    private readonly MemoryGrainStorageTestFixture _fixture;

    public MemoryGrainStorageTests(MemoryGrainStorageTestFixture fixture)
        : base(fixture.Storage)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ProviderViolation_ThrowsFrameworkNeutralException()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var runner = new TestRunner(new NullStateGrainStorage());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.PersistenceStorage_WriteRead_StringKeyAsync(cancellationToken));

        Assert.Contains("found '<null>'", exception.Message);
    }

    [Fact]
    public async Task StoreWriteRead_NullState_ThrowsBeforeCallingStorage()
    {
        var storage = new TrackingGrainStorage();
        var runner = new TestRunner(storage);

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => runner.StoreWriteReadWithNullState(TestContext.Current.CancellationToken));

        Assert.Equal("grainState", exception.ParamName);
        Assert.Equal(0, storage.CallCount);
    }

    [Fact]
    public async Task StoreWriteClearRead_NullState_ThrowsBeforeCallingStorage()
    {
        var storage = new TrackingGrainStorage();
        var runner = new TestRunner(storage);

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => runner.StoreWriteClearReadWithNullState(TestContext.Current.CancellationToken));

        Assert.Equal("grainState", exception.ParamName);
        Assert.Equal(0, storage.CallCount);
    }

    [Fact]
    public async Task StoreWriteRead_CanceledTokenTakesPrecedenceOverNullState()
    {
        var storage = new TrackingGrainStorage();
        var runner = new TestRunner(storage);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => runner.StoreWriteReadWithNullState(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(0, storage.CallCount);
    }

    [Fact]
    public async Task StoreWriteClearRead_CanceledTokenTakesPrecedenceOverNullState()
    {
        var storage = new TrackingGrainStorage();
        var runner = new TestRunner(storage);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => runner.StoreWriteClearReadWithNullState(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(0, storage.CallCount);
    }

    private sealed class TestRunner(IGrainStorage storage) : GrainStorageTestRunner(storage)
    {
        public Task StoreWriteReadWithNullState(CancellationToken cancellationToken = default) =>
            Store_WriteRead<object>("grain-type", default, null!, cancellationToken);

        public Task StoreWriteClearReadWithNullState(CancellationToken cancellationToken = default) =>
            Store_WriteClearRead<object>("grain-type", default, null!, cancellationToken);
    }

    private sealed class NullStateGrainStorage : IGrainStorage
    {
        public Task ReadStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            grainState.State = default!;
            return Task.CompletedTask;
        }

        public Task WriteStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            grainState.RecordExists = true;
            return Task.CompletedTask;
        }

        public Task ClearStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState) => Task.CompletedTask;
    }

    private sealed class TrackingGrainStorage : IGrainStorage
    {
        public int CallCount { get; private set; }

        public Task ReadStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            CallCount++;
            return Task.CompletedTask;
        }

        public Task WriteStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            CallCount++;
            return Task.CompletedTask;
        }

        public Task ClearStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            CallCount++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public override Task PersistenceStorage_WriteReadIdCyrillic()
    {
        return base.PersistenceStorage_WriteReadIdCyrillicAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public override Task PersistenceStorage_WriteDuplicateFailsWithInconsistentStateException()
    {
        return base.PersistenceStorage_WriteDuplicateFailsWithInconsistentStateExceptionAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public override Task PersistenceStorage_WriteInconsistentFailsWithInconsistentStateException()
    {
        return base.PersistenceStorage_WriteInconsistentFailsWithInconsistentStateExceptionAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public override Task PersistenceStorage_WriteReadWriteReadStatesInParallel()
    {
        return RunPersistenceStorage_WriteReadWriteReadStatesInParallel("MemoryTest", 50, TestContext.Current.CancellationToken);
    }

    [Fact]
    public override Task PersistenceStorage_ReadNonExistentState()
    {
        return base.PersistenceStorage_ReadNonExistentStateAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public override Task PersistenceStorage_ReadNonExistentStateHasNonNullState()
    {
        return base.PersistenceStorage_ReadNonExistentStateHasNonNullStateAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public override Task PersistenceStorage_WriteClearWrite()
    {
        return base.PersistenceStorage_WriteClearWriteAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public override Task PersistenceStorage_WriteClearRead()
    {
        return base.PersistenceStorage_WriteClearReadAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public override Task PersistenceStorage_WriteReadClearReadCycle()
    {
        return base.PersistenceStorage_WriteReadClearReadCycleAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public override Task PersistenceStorage_WriteRead_StringKey()
    {
        return base.PersistenceStorage_WriteRead_StringKeyAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public override Task PersistenceStorage_WriteRead_IntegerKey()
    {
        return base.PersistenceStorage_WriteRead_IntegerKeyAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public override Task PersistenceStorage_ETagChangesOnWrite()
    {
        return base.PersistenceStorage_ETagChangesOnWriteAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public override Task PersistenceStorage_ClearBeforeWrite()
    {
        return base.PersistenceStorage_ClearBeforeWriteAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public override Task PersistenceStorage_ClearStateDoesNotNullifyState()
    {
        return base.PersistenceStorage_ClearStateDoesNotNullifyStateAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public override Task PersistenceStorage_ClearUpdatesETag()
    {
        return base.PersistenceStorage_ClearUpdatesETagAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public override Task PersistenceStorage_ReadAfterClear()
    {
        return base.PersistenceStorage_ReadAfterClearAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public override Task PersistenceStorage_MultipleClearOperations()
    {
        return base.PersistenceStorage_MultipleClearOperationsAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public override Task PersistenceStorage_WriteWithSameValuesUpdatesETag()
    {
        return base.PersistenceStorage_WriteWithSameValuesUpdatesETagAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public override Task PersistenceStorage_StateNamesUseIndependentRecords()
    {
        return base.PersistenceStorage_StateNamesUseIndependentRecordsAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public override Task PersistenceStorage_ClearInconsistentFailsWithInconsistentStateException()
    {
        return base.PersistenceStorage_ClearInconsistentFailsWithInconsistentStateExceptionAsync(TestContext.Current.CancellationToken);
    }

    [Fact, TestCategory("ModelBased")]
    public Task GrainStorage_ModelBasedGeneratedConformance()
    {
        var runner = new GrainStorageModelBasedTestRunner(Storage, "MemoryStore");
        return runner.RunGeneratedConformanceTests(TestContext.Current.CancellationToken);
    }

    [Fact, TestCategory("ModelBased")]
    public Task GrainStorageWithLatency_ModelBasedGeneratedConformance()
    {
        var runner = new GrainStorageModelBasedTestRunner(_fixture.CreateStorageWithLatency(), "MemoryStoreWithLatency");
        return runner.RunGeneratedConformanceTests(TestContext.Current.CancellationToken);
    }

    [Fact, TestCategory("ModelBased")]
    public async Task GrainStorage_ModelBasedGeneratedConformance_ReportsInvalidProviderBehavior()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        string? failureOutput = null;
        var runner = new GrainStorageModelBasedTestRunner(
            new InvalidGrainStorage(),
            "InvalidStorage",
            message => failureOutput = message);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunGeneratedConformanceTests(cancellationToken));

        Assert.Contains("Model-based grain storage conformance test failed.", exception.Message);
        Assert.NotNull(failureOutput);
        Assert.Contains("Successful write did not return a non-empty ETag.", failureOutput);
    }

    [Fact, TestCategory("ModelBased")]
    public async Task GrainStorage_ModelBasedGeneratedConformance_ExcludesCurrentETagSameValueWrites()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var storage = new StableNoOpETagGrainStorage();
        var runner = new GrainStorageModelBasedTestRunner(storage, "StableNoOpETagStorage");

        await runner.RunGeneratedConformanceTests(cancellationToken);

        Assert.Equal(0, storage.SameValueWriteCount);
    }

    private sealed class InvalidGrainStorage : IGrainStorage
    {
        public Task ReadStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState) => Task.CompletedTask;

        public Task WriteStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            grainState.RecordExists = true;
            return Task.CompletedTask;
        }

        public Task ClearStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState) => Task.CompletedTask;
    }

    private sealed class StableNoOpETagGrainStorage : IGrainStorage
    {
        private readonly Dictionary<GrainId, (TestState1? State, string ETag)> records = [];
        private int etag;

        public int SameValueWriteCount { get; private set; }

        public Task ReadStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            if (records.TryGetValue(grainId, out var record))
            {
                grainState.State = (T)(object)(record.State is null ? new TestState1() : Clone(record.State));
                grainState.ETag = record.ETag;
                grainState.RecordExists = record.State is not null;
            }
            else
            {
                grainState.ETag = null;
                grainState.RecordExists = false;
            }

            return Task.CompletedTask;
        }

        public Task WriteStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            var state = (TestState1)(object)grainState.State!;
            if (records.TryGetValue(grainId, out var record))
            {
                if (grainState.ETag != record.ETag)
                {
                    throw new InconsistentStateException("ETag mismatch.");
                }

                if (record.State is not null && state.Equals(record.State))
                {
                    SameValueWriteCount++;
                    grainState.ETag = record.ETag;
                    grainState.RecordExists = true;
                    return Task.CompletedTask;
                }
            }
            else if (grainState.ETag is not null)
            {
                throw new InconsistentStateException("ETag mismatch.");
            }

            var nextETag = $"etag-{++etag}";
            records[grainId] = (Clone(state), nextETag);
            grainState.ETag = nextETag;
            grainState.RecordExists = true;
            return Task.CompletedTask;
        }

        public Task ClearStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            if (!records.TryGetValue(grainId, out var record) || grainState.ETag != record.ETag)
            {
                throw new InconsistentStateException("ETag mismatch.");
            }

            var nextETag = $"etag-{++etag}";
            records[grainId] = (null, nextETag);
            grainState.State = (T)(object)new TestState1();
            grainState.ETag = nextETag;
            grainState.RecordExists = false;
            return Task.CompletedTask;
        }

        private static TestState1 Clone(TestState1 state) => new() { A = state.A, B = state.B, C = state.C };
    }
}
