using Orleans.Persistence.EntityFrameworkCore.SqlServer.Data;
using TestExtensions;
using TestExtensions.Runners;
using Xunit.Abstractions;

namespace Tester.EFCore;

[TestCategory("Persistence"), TestCategory("EFCore"), TestCategory("EFCore-SqlServer")]
public class PersistenceGrainTests_EFCoreSqlServerGrainStorage : OrleansTestingBase, IClassFixture<EFCoreFixture<SqlServerGrainStateDbContext>>
{
    private readonly GrainPersistenceTestsRunner _runner;

    public PersistenceGrainTests_EFCoreSqlServerGrainStorage(
        ITestOutputHelper output, EFCoreFixture<SqlServerGrainStateDbContext> fixture, string grainNamespace = "UnitTests.Grains")
    {
        fixture.EnsurePreconditionsMet();
        this._runner = new GrainPersistenceTestsRunner(output, fixture, grainNamespace);
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_EFCoreSqlServerGrainStorage_Delete() => await _runner.Grain_GrainStorage_Delete();

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_EFCoreSqlServerGrainStorage_Read() => await _runner.Grain_GrainStorage_Read();

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_GuidKey_EFCoreSqlServerGrainStorage_Read_Write() => await _runner.Grain_GuidKey_GrainStorage_Read_Write();

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_LongKey_EFCoreSqlServerGrainStorage_Read_Write() => await _runner.Grain_LongKey_GrainStorage_Read_Write();

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_LongKeyExtended_EFCoreSqlServerGrainStorage_Read_Write() => await _runner.Grain_LongKeyExtended_GrainStorage_Read_Write();

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_GuidKeyExtended_EFCoreSqlServerGrainStorage_Read_Write() => await _runner.Grain_GuidKeyExtended_GrainStorage_Read_Write();

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_Generic_EFCoreSqlServerGrainStorage_Read_Write() => await _runner.Grain_Generic_GrainStorage_Read_Write();

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_Generic_EFCoreSqlServerGrainStorage_DiffTypes() => await _runner.Grain_Generic_GrainStorage_DiffTypes();

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_EFCoreSqlServerGrainStorage_SiloRestart() => await _runner.Grain_GrainStorage_SiloRestart();
}