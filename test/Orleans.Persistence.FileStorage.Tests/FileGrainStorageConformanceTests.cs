using Orleans.Persistence.TestKit;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Persistence.FileStorage.Tests;

[TestProvider("None"), TestSuite("BVT"), TestCategory("FileStorage"), TestCategory("Persistence")]
public sealed class FileGrainStorageConformanceTests
    : GrainStorageTestRunner, IClassFixture<FileGrainStorageTestFixture>
{
    public FileGrainStorageConformanceTests(FileGrainStorageTestFixture fixture)
        : base(fixture.Storage)
    {
    }

    [Fact]
    public Task PersistenceStorage_ReadIfNotExists() =>
        base.PersistenceStorage_ReadNonExistentStateAsync(TestContext.Current.CancellationToken);

    [Fact]
    public Task PersistenceStorage_WriteRead() =>
        base.PersistenceStorage_WriteReadIdCyrillicAsync(TestContext.Current.CancellationToken);

    [Fact]
    public Task PersistenceStorage_WriteReadWithIntegerKey() =>
        base.PersistenceStorage_WriteRead_IntegerKeyAsync(TestContext.Current.CancellationToken);

    [Fact]
    public Task PersistenceStorage_WriteReadWithStringKey() =>
        base.PersistenceStorage_WriteRead_StringKeyAsync(TestContext.Current.CancellationToken);

    [Fact]
    public Task PersistenceStorage_StateNamesAreIndependent() =>
        base.PersistenceStorage_StateNamesUseIndependentRecordsAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task PersistenceStorage_WriteReadWriteRead()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var grainId = GrainId.Create("write-read-write-read", Guid.NewGuid().ToString("N"));
        var written = new GrainState<FileStorageTestState>(
            new FileStorageTestState { Value = "first", Revision = 1 });
        await Storage.WriteStateAsync("state", grainId, written, cancellationToken);
        var firstETag = Assert.IsType<string>(written.ETag);
        var firstRead = new GrainState<FileStorageTestState>(new FileStorageTestState());
        await Storage.ReadStateAsync("state", grainId, firstRead, cancellationToken);

        Assert.Equal(new FileStorageTestState { Value = "first", Revision = 1 }, firstRead.State);
        Assert.Equal(firstETag, firstRead.ETag);
        Assert.True(firstRead.RecordExists);

        written.State = new FileStorageTestState { Value = "second", Revision = 2 };
        await Storage.WriteStateAsync("state", grainId, written, cancellationToken);
        var secondETag = Assert.IsType<string>(written.ETag);
        var secondRead = new GrainState<FileStorageTestState>(new FileStorageTestState());
        await Storage.ReadStateAsync("state", grainId, secondRead, cancellationToken);

        Assert.NotEqual(firstETag, secondETag);
        Assert.Equal(new FileStorageTestState { Value = "second", Revision = 2 }, secondRead.State);
        Assert.Equal(secondETag, secondRead.ETag);
        Assert.True(secondRead.RecordExists);
    }

    [Fact]
    public Task PersistenceStorage_Delete() =>
        base.PersistenceStorage_WriteReadClearReadCycleAsync(TestContext.Current.CancellationToken);

    [Fact]
    public Task PersistenceStorage_DeleteIfNotExists() =>
        base.PersistenceStorage_ClearBeforeWriteAsync(TestContext.Current.CancellationToken);

    [Fact]
    public override Task PersistenceStorage_WriteClearWrite() =>
        base.PersistenceStorage_WriteClearWriteAsync(TestContext.Current.CancellationToken);

    [Fact]
    public override Task PersistenceStorage_WriteClearRead() =>
        base.PersistenceStorage_WriteClearReadAsync(TestContext.Current.CancellationToken);

    [Fact]
    public override Task PersistenceStorage_ClearStateDoesNotNullifyState() =>
        base.PersistenceStorage_ClearStateDoesNotNullifyStateAsync(TestContext.Current.CancellationToken);

    [Fact]
    public override Task PersistenceStorage_ClearUpdatesETag() =>
        base.PersistenceStorage_ClearUpdatesETagAsync(TestContext.Current.CancellationToken);

    [Fact]
    public override Task PersistenceStorage_ReadAfterClear() =>
        base.PersistenceStorage_ReadAfterClearAsync(TestContext.Current.CancellationToken);

    [Fact]
    public override Task PersistenceStorage_MultipleClearOperations() =>
        base.PersistenceStorage_MultipleClearOperationsAsync(TestContext.Current.CancellationToken);

    [Fact]
    public override Task PersistenceStorage_ReadNonExistentStateHasNonNullState() =>
        base.PersistenceStorage_ReadNonExistentStateHasNonNullStateAsync(TestContext.Current.CancellationToken);

    [Fact]
    public override Task PersistenceStorage_ETagChangesOnWrite() =>
        base.PersistenceStorage_ETagChangesOnWriteAsync(TestContext.Current.CancellationToken);

    [Fact]
    public override Task PersistenceStorage_WriteWithSameValuesUpdatesETag() =>
        base.PersistenceStorage_WriteWithSameValuesUpdatesETagAsync(TestContext.Current.CancellationToken);

    [Fact]
    public Task PersistenceStorage_DuplicateWriteThrowsInconsistentStateException() =>
        base.PersistenceStorage_WriteDuplicateFailsWithInconsistentStateExceptionAsync(
            TestContext.Current.CancellationToken);

    [Fact]
    public Task PersistenceStorage_StaleWriteThrowsInconsistentStateException() =>
        base.PersistenceStorage_WriteInconsistentFailsWithInconsistentStateExceptionAsync(
            TestContext.Current.CancellationToken);

    [Fact]
    public Task PersistenceStorage_StaleClearThrowsInconsistentStateException() =>
        base.PersistenceStorage_ClearInconsistentFailsWithInconsistentStateExceptionAsync(
            TestContext.Current.CancellationToken);

    [Fact]
    public Task PersistenceStorage_ParallelWritesRespectETags() =>
        base.PersistenceStorage_WriteReadWriteReadStatesInParallelAsync(TestContext.Current.CancellationToken);
}
