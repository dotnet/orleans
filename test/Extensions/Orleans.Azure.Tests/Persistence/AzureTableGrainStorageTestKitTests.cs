using Orleans.Hosting;
using Orleans.Persistence.TestKit;
using TestExtensions;
using Xunit;

namespace Tester.AzureUtils.Persistence;

/// <summary>
/// Tests for AzureTableGrainStorage using the Orleans.Persistence.TestKit.
/// These tests validate the storage provider behavior at the IGrainStorage interface level.
/// </summary>
[TestCategory("Persistence"), TestCategory("AzureStorage")]
public class AzureTableGrainStorageTestKitTests : GrainStorageTestRunner, IClassFixture<AzureTableGrainStorageTestKitTests.Fixture>
{
    public class Fixture : GrainStorageTestFixture, IAsyncLifetime
    {
        protected override string StorageProviderName => "AzureTableStore";

        protected override void CheckPreconditionsOrThrow()
        {
            TestUtils.CheckForAzureStorage();
        }

        protected override void ConfigureSilo(ISiloBuilder siloBuilder)
        {
            siloBuilder.AddAzureTableGrainStorage(StorageProviderName, options =>
            {
                options.ConfigureTestDefaults();
            });
        }
    }

    public AzureTableGrainStorageTestKitTests(Fixture fixture)
        : base(fixture.Storage)
    {
        fixture.EnsurePreconditionsMet();
    }

    [Fact]
    public override Task PersistenceStorage_WriteReadIdCyrillic()
    {
        return base.PersistenceStorage_WriteReadIdCyrillic();
    }

    [Fact]
    public override Task PersistenceStorage_WriteDuplicateFailsWithInconsistentStateException()
    {
        return base.PersistenceStorage_WriteDuplicateFailsWithInconsistentStateException();
    }

    [Fact]
    public override Task PersistenceStorage_WriteInconsistentFailsWithInconsistentStateException()
    {
        return base.PersistenceStorage_WriteInconsistentFailsWithInconsistentStateException();
    }

    [Fact]
    public override Task PersistenceStorage_WriteReadWriteReadStatesInParallel()
    {
        return RunPersistenceStorage_WriteReadWriteReadStatesInParallel("AzureTableTest", 50);
    }

    [Fact]
    public override Task PersistenceStorage_ReadNonExistentState()
    {
        return base.PersistenceStorage_ReadNonExistentState();
    }

    [Fact]
    public override Task PersistenceStorage_ReadNonExistentStateHasNonNullState()
    {
        return base.PersistenceStorage_ReadNonExistentStateHasNonNullState();
    }

    [Fact]
    public override Task PersistenceStorage_WriteClearWrite()
    {
        return base.PersistenceStorage_WriteClearWrite();
    }

    [Fact]
    public override Task PersistenceStorage_WriteClearRead()
    {
        return base.PersistenceStorage_WriteClearRead();
    }

    [Fact]
    public override Task PersistenceStorage_WriteReadClearReadCycle()
    {
        return base.PersistenceStorage_WriteReadClearReadCycle();
    }

    [Fact]
    public override Task PersistenceStorage_WriteRead_StringKey()
    {
        return base.PersistenceStorage_WriteRead_StringKey();
    }

    [Fact]
    public override Task PersistenceStorage_WriteRead_IntegerKey()
    {
        return base.PersistenceStorage_WriteRead_IntegerKey();
    }

    [Fact]
    public override Task PersistenceStorage_ETagChangesOnWrite()
    {
        return base.PersistenceStorage_ETagChangesOnWrite();
    }

    [Fact]
    public override Task PersistenceStorage_ClearBeforeWrite()
    {
        return base.PersistenceStorage_ClearBeforeWrite();
    }

    [Fact]
    public override Task PersistenceStorage_ClearStateDoesNotNullifyState()
    {
        return base.PersistenceStorage_ClearStateDoesNotNullifyState();
    }

    [Fact]
    public override Task PersistenceStorage_ClearUpdatesETag()
    {
        return base.PersistenceStorage_ClearUpdatesETag();
    }

    [Fact]
    public override Task PersistenceStorage_ReadAfterClear()
    {
        return base.PersistenceStorage_ReadAfterClear();
    }

    [Fact]
    public override Task PersistenceStorage_MultipleClearOperations()
    {
        return base.PersistenceStorage_MultipleClearOperations();
    }

    [Fact]
    public override Task PersistenceStorage_WriteWithSameValuesUpdatesETag()
    {
        return base.PersistenceStorage_WriteWithSameValuesUpdatesETag();
    }

    [Fact]
    public override Task PersistenceStorage_StateNamesUseIndependentRecords()
    {
        return base.PersistenceStorage_StateNamesUseIndependentRecords();
    }

    [Fact]
    public override Task PersistenceStorage_ClearInconsistentFailsWithInconsistentStateException()
    {
        return base.PersistenceStorage_ClearInconsistentFailsWithInconsistentStateException();
    }
}
