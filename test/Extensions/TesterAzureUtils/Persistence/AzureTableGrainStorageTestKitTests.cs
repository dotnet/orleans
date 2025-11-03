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
    public Task BasicWriteRead()
    {
        return PersistenceStorage_WriteReadIdCyrillic();
    }

    [SkippableFact]
    public Task DuplicateInsertFails()
    {
        return PersistenceStorage_WriteDuplicateFailsWithInconsistentStateException();
    }

    [SkippableFact]
    public Task InconsistentETagFails()
    {
        return PersistenceStorage_WriteInconsistentFailsWithInconsistentStateException();
    }

    [SkippableFact]
    public Task ConcurrentOperations()
    {
        return PersistenceStorage_WriteReadWriteReadStatesInParallel("AzureTableTest", 50);
    }

    [SkippableFact]
    public Task ReadNonExistent()
    {
        return PersistenceStorage_ReadNonExistentState();
    }

    [SkippableFact]
    public Task WriteClearWriteCycle()
    {
        return PersistenceStorage_WriteClearWrite();
    }
}
