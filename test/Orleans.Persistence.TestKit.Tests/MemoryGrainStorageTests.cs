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
    public Task PersistenceStorage_WriteReadIdCyrillic()
    {
        return base.PersistenceStorage_WriteReadIdCyrillic();
    }

    [Fact]
    public Task PersistenceStorage_WriteDuplicateFailsWithInconsistentStateException()
    {
        return base.PersistenceStorage_WriteDuplicateFailsWithInconsistentStateException();
    }

    [Fact]
    public Task PersistenceStorage_WriteInconsistentFailsWithInconsistentStateException()
    {
        return base.PersistenceStorage_WriteInconsistentFailsWithInconsistentStateException();
    }

    [Fact]
    public Task PersistenceStorage_WriteReadWriteReadStatesInParallel()
    {
        return base.PersistenceStorage_WriteReadWriteReadStatesInParallel("MemoryTest", 50);
    }

    [Fact]
    public Task PersistenceStorage_ReadNonExistentState()
    {
        return base.PersistenceStorage_ReadNonExistentState();
    }

    [Fact]
    public Task PersistenceStorage_WriteClearWrite()
    {
        return base.PersistenceStorage_WriteClearWrite();
    }
}
