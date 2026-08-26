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
            () => runner.PersistenceStorage_WriteRead_StringKey(cancellationToken));

        Assert.Contains("found '<null>'", exception.Message);
    }

    private sealed class TestRunner(IGrainStorage storage) : GrainStorageTestRunner(storage);

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

    [Fact]
    public override Task PersistenceStorage_WriteReadIdCyrillic()
    {
        return base.PersistenceStorage_WriteReadIdCyrillic(TestContext.Current.CancellationToken);
    }

    [Fact]
    public override Task PersistenceStorage_WriteDuplicateFailsWithInconsistentStateException()
    {
        return base.PersistenceStorage_WriteDuplicateFailsWithInconsistentStateException(TestContext.Current.CancellationToken);
    }

    [Fact]
    public override Task PersistenceStorage_WriteInconsistentFailsWithInconsistentStateException()
    {
        return base.PersistenceStorage_WriteInconsistentFailsWithInconsistentStateException(TestContext.Current.CancellationToken);
    }

    [Fact]
    public override Task PersistenceStorage_WriteReadWriteReadStatesInParallel()
    {
        return RunPersistenceStorage_WriteReadWriteReadStatesInParallel("MemoryTest", 50, TestContext.Current.CancellationToken);
    }

    [Fact]
    public override Task PersistenceStorage_ReadNonExistentState()
    {
        return base.PersistenceStorage_ReadNonExistentState(TestContext.Current.CancellationToken);
    }

    [Fact]
    public override Task PersistenceStorage_ReadNonExistentStateHasNonNullState()
    {
        return base.PersistenceStorage_ReadNonExistentStateHasNonNullState(TestContext.Current.CancellationToken);
    }

    [Fact]
    public override Task PersistenceStorage_WriteClearWrite()
    {
        return base.PersistenceStorage_WriteClearWrite(TestContext.Current.CancellationToken);
    }

    [Fact]
    public override Task PersistenceStorage_WriteClearRead()
    {
        return base.PersistenceStorage_WriteClearRead(TestContext.Current.CancellationToken);
    }

    [Fact]
    public override Task PersistenceStorage_WriteReadClearReadCycle()
    {
        return base.PersistenceStorage_WriteReadClearReadCycle(TestContext.Current.CancellationToken);
    }

    [Fact]
    public override Task PersistenceStorage_WriteRead_StringKey()
    {
        return base.PersistenceStorage_WriteRead_StringKey(TestContext.Current.CancellationToken);
    }

    [Fact]
    public override Task PersistenceStorage_WriteRead_IntegerKey()
    {
        return base.PersistenceStorage_WriteRead_IntegerKey(TestContext.Current.CancellationToken);
    }

    [Fact]
    public override Task PersistenceStorage_ETagChangesOnWrite()
    {
        return base.PersistenceStorage_ETagChangesOnWrite(TestContext.Current.CancellationToken);
    }

    [Fact]
    public override Task PersistenceStorage_ClearBeforeWrite()
    {
        return base.PersistenceStorage_ClearBeforeWrite(TestContext.Current.CancellationToken);
    }

    [Fact]
    public override Task PersistenceStorage_ClearStateDoesNotNullifyState()
    {
        return base.PersistenceStorage_ClearStateDoesNotNullifyState(TestContext.Current.CancellationToken);
    }

    [Fact]
    public override Task PersistenceStorage_ClearUpdatesETag()
    {
        return base.PersistenceStorage_ClearUpdatesETag(TestContext.Current.CancellationToken);
    }

    [Fact]
    public override Task PersistenceStorage_ReadAfterClear()
    {
        return base.PersistenceStorage_ReadAfterClear(TestContext.Current.CancellationToken);
    }

    [Fact]
    public override Task PersistenceStorage_MultipleClearOperations()
    {
        return base.PersistenceStorage_MultipleClearOperations(TestContext.Current.CancellationToken);
    }

    [Fact]
    public override Task PersistenceStorage_WriteWithSameValuesUpdatesETag()
    {
        return base.PersistenceStorage_WriteWithSameValuesUpdatesETag(TestContext.Current.CancellationToken);
    }

    [Fact]
    public override Task PersistenceStorage_StateNamesUseIndependentRecords()
    {
        return base.PersistenceStorage_StateNamesUseIndependentRecords(TestContext.Current.CancellationToken);
    }

    [Fact]
    public override Task PersistenceStorage_ClearInconsistentFailsWithInconsistentStateException()
    {
        return base.PersistenceStorage_ClearInconsistentFailsWithInconsistentStateException(TestContext.Current.CancellationToken);
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
