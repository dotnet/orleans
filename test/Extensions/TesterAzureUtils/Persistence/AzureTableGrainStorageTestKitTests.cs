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
    public Task PersistenceStorage_WriteClearWrite()
    {
        return base.PersistenceStorage_WriteClearWrite();
    }
}
