using Orleans.Hosting;
using Orleans.Persistence.TestKit;
using Xunit;

namespace Orleans.Persistence.TestKit.Package.Tests;

public sealed class PersistenceTestKitPackageFixture : GrainStorageTestFixture
{
    protected override string StorageProviderName => "PackageSmokeStorage";

    protected override void ConfigureSilo(ISiloBuilder siloBuilder)
    {
        siloBuilder.AddMemoryGrainStorage(StorageProviderName);
    }
}

public sealed class PersistenceTestKitPackageConsumerTests(
    PersistenceTestKitPackageFixture fixture)
    : GrainStorageTestRunner(fixture.Storage),
        IClassFixture<PersistenceTestKitPackageFixture>
{
    [Fact]
    public override Task PersistenceStorage_WriteRead_StringKey()
    {
        return base.PersistenceStorage_WriteRead_StringKey();
    }
}
