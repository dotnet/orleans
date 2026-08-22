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
        base.PersistenceStorage_ReadNonExistentState();

    [Fact]
    public Task PersistenceStorage_WriteRead() =>
        base.PersistenceStorage_WriteReadIdCyrillic();

    [Fact]
    public Task PersistenceStorage_WriteReadWithIntegerKey() =>
        base.PersistenceStorage_WriteRead_IntegerKey();

    [Fact]
    public Task PersistenceStorage_WriteReadWithStringKey() =>
        base.PersistenceStorage_WriteRead_StringKey();

    [Fact]
    public Task PersistenceStorage_StateNamesAreIndependent() =>
        base.PersistenceStorage_StateNamesUseIndependentRecords();

    [Fact]
    public async Task PersistenceStorage_WriteReadWriteRead()
    {
        var grainId = GrainId.Create("write-read-write-read", Guid.NewGuid().ToString("N"));
        var written = new GrainState<FileStorageTestState>(
            new FileStorageTestState { Value = "first", Revision = 1 });
        await Storage.WriteStateAsync("state", grainId, written);
        var firstETag = Assert.IsType<string>(written.ETag);
        var firstRead = new GrainState<FileStorageTestState>(new FileStorageTestState());
        await Storage.ReadStateAsync("state", grainId, firstRead);

        Assert.Equal(new FileStorageTestState { Value = "first", Revision = 1 }, firstRead.State);
        Assert.Equal(firstETag, firstRead.ETag);
        Assert.True(firstRead.RecordExists);

        written.State = new FileStorageTestState { Value = "second", Revision = 2 };
        await Storage.WriteStateAsync("state", grainId, written);
        var secondETag = Assert.IsType<string>(written.ETag);
        var secondRead = new GrainState<FileStorageTestState>(new FileStorageTestState());
        await Storage.ReadStateAsync("state", grainId, secondRead);

        Assert.NotEqual(firstETag, secondETag);
        Assert.Equal(new FileStorageTestState { Value = "second", Revision = 2 }, secondRead.State);
        Assert.Equal(secondETag, secondRead.ETag);
        Assert.True(secondRead.RecordExists);
    }

    [Fact]
    public Task PersistenceStorage_Delete() =>
        base.PersistenceStorage_WriteReadClearReadCycle();

    [Fact]
    public Task PersistenceStorage_DeleteIfNotExists() =>
        base.PersistenceStorage_ClearBeforeWrite();

    [Fact]
    public override Task PersistenceStorage_ETagChangesOnWrite() =>
        base.PersistenceStorage_ETagChangesOnWrite();

    [Fact]
    public override Task PersistenceStorage_WriteWithSameValuesUpdatesETag() =>
        base.PersistenceStorage_WriteWithSameValuesUpdatesETag();

    [Fact]
    public Task PersistenceStorage_DuplicateWriteThrowsInconsistentStateException() =>
        base.PersistenceStorage_WriteDuplicateFailsWithInconsistentStateException();

    [Fact]
    public Task PersistenceStorage_StaleWriteThrowsInconsistentStateException() =>
        base.PersistenceStorage_WriteInconsistentFailsWithInconsistentStateException();

    [Fact]
    public Task PersistenceStorage_StaleClearThrowsInconsistentStateException() =>
        base.PersistenceStorage_ClearInconsistentFailsWithInconsistentStateException();

    [Fact]
    public Task PersistenceStorage_ParallelWritesRespectETags() =>
        base.PersistenceStorage_WriteReadWriteReadStatesInParallel();
}
