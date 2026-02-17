using Orleans.Hosting;
using Orleans.Persistence.TestKit;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace Tester.AzureUtils.Persistence;

/// <summary>
/// Tests for AzureTableGrainStorage using the Orleans.Persistence.TestKit.
/// These tests validate the storage provider behavior at the IGrainStorage interface level.
/// </summary>
[TestCategory("Persistence"), TestCategory("AzureStorage")]
public class AzureTableGrainStorageTestKitTests : GrainStorageTestRunner, IClassFixture<AzureTableGrainStorageTestKitTests.Fixture>
{
    public class Fixture : GrainStorageTestFixture
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

    private readonly ITestOutputHelper _output;

    public AzureTableGrainStorageTestKitTests(ITestOutputHelper output, Fixture fixture)
        : base(fixture.Storage)
    {
        _output = output;
        fixture.EnsurePreconditionsMet();
    }

    [SkippableFact]
    public Task PersistenceStorage_WriteReadIdCyrillic()
    {
        return base.PersistenceStorage_WriteReadIdCyrillic();
    }

    [SkippableFact]
    public Task PersistenceStorage_WriteDuplicateFailsWithInconsistentStateException()
    {
        return base.PersistenceStorage_WriteDuplicateFailsWithInconsistentStateException();
    }

    [SkippableFact]
    public Task PersistenceStorage_WriteInconsistentFailsWithInconsistentStateException()
    {
        return base.PersistenceStorage_WriteInconsistentFailsWithInconsistentStateException();
    }

    [SkippableFact]
    public Task PersistenceStorage_WriteReadWriteReadStatesInParallel()
    {
        return base.PersistenceStorage_WriteReadWriteReadStatesInParallel("AzureTableTest", 50);
    }

    [SkippableFact]
    public Task PersistenceStorage_ReadNonExistentState()
    {
        return base.PersistenceStorage_ReadNonExistentState();
    }

    [SkippableFact]
    public Task PersistenceStorage_ReadNonExistentStateHasNonNullState()
    {
        return base.PersistenceStorage_ReadNonExistentStateHasNonNullState();
    }

    [SkippableFact]
    public Task PersistenceStorage_WriteClearWrite()
    {
        return base.PersistenceStorage_WriteClearWrite();
    }

    [SkippableFact]
    public Task PersistenceStorage_WriteClearRead()
    {
        return base.PersistenceStorage_WriteClearRead();
    }

    [SkippableFact]
    public Task PersistenceStorage_WriteReadClearReadCycle()
    {
        return base.PersistenceStorage_WriteReadClearReadCycle();
    }

    [SkippableFact]
    public Task PersistenceStorage_WriteRead_StringKey()
    {
        return base.PersistenceStorage_WriteRead_StringKey();
    }

    [SkippableFact]
    public Task PersistenceStorage_WriteRead_IntegerKey()
    {
        return base.PersistenceStorage_WriteRead_IntegerKey();
    }

    [SkippableFact]
    public Task PersistenceStorage_ETagChangesOnWrite()
    {
        return base.PersistenceStorage_ETagChangesOnWrite();
    }

    [SkippableFact]
    public Task PersistenceStorage_ClearBeforeWrite()
    {
        return base.PersistenceStorage_ClearBeforeWrite();
    }

    [SkippableFact]
    public Task PersistenceStorage_ClearStateDoesNotNullifyState()
    {
        return base.PersistenceStorage_ClearStateDoesNotNullifyState();
    }

    [SkippableFact]
    public Task PersistenceStorage_ClearUpdatesETag()
    {
        return base.PersistenceStorage_ClearUpdatesETag();
    }

    [SkippableFact]
    public Task PersistenceStorage_ReadAfterClear()
    {
        return base.PersistenceStorage_ReadAfterClear();
    }

    [SkippableFact]
    public Task PersistenceStorage_MultipleClearOperations()
    {
        return base.PersistenceStorage_MultipleClearOperations();
    }

    [SkippableFact]
    public Task PersistenceStorage_WriteWithSameValuesUpdatesETag()
    {
        return base.PersistenceStorage_WriteWithSameValuesUpdatesETag();
    }
}
