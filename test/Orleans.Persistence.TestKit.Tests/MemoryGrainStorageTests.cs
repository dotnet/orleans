using Orleans.Hosting;
using Orleans.Persistence.TestKit;
using Xunit;

namespace Orleans.Persistence.Memory.Tests;

/// <summary>
/// Example test fixture showing how to configure MemoryGrainStorage for testing.
/// </summary>
public class MemoryGrainStorageTestFixture : GrainStorageTestFixture
{
    protected override string StorageProviderName => "MemoryStore";

    protected override void ConfigureSilo(ISiloBuilder siloBuilder)
    {
        siloBuilder.AddMemoryGrainStorage("MemoryStore");
    }
}

/// <summary>
/// Example tests demonstrating how to use the Orleans.Persistence.TestKit
/// to test the MemoryGrainStorage provider.
/// </summary>
[TestCategory("Persistence"), TestCategory("MemoryStore")]
public class MemoryGrainStorageTests : GrainStorageTestRunner, IClassFixture<MemoryGrainStorageTestFixture>
{
    public MemoryGrainStorageTests(MemoryGrainStorageTestFixture fixture)
        : base(fixture.Storage)
    {
    }

    [Fact]
    public Task BasicWriteRead()
    {
        return PersistenceStorage_WriteReadIdCyrillic();
    }

    [Fact]
    public Task DuplicateInsertFails()
    {
        return PersistenceStorage_WriteDuplicateFailsWithInconsistentStateException();
    }

    [Fact]
    public Task InconsistentETagFails()
    {
        return PersistenceStorage_WriteInconsistentFailsWithInconsistentStateException();
    }

    [Fact]
    public Task ConcurrentOperations()
    {
        return PersistenceStorage_WriteReadWriteReadStatesInParallel("MemoryTest", 50);
    }
}
