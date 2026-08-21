using Orleans.EntityFrameworkCore.Tests.Infrastructure;
using Orleans.Persistence.EntityFrameworkCore.MySql.Data;
using Orleans.Persistence.EntityFrameworkCore.PostgreSQL.Data;
using Orleans.Persistence.EntityFrameworkCore.SqlServer.Data;
using TestExtensions;
using TestExtensions.Runners;

namespace Orleans.EntityFrameworkCore.Tests.Persistence;

[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestArea("EFCore")]
[TestSuite("Functional")]
[TestProvider(EFCoreTestCategories.SqlServer)]
[TestArea("Persistence")]
public sealed class PersistenceGrainTests_EFCoreSqlServerMatrixGrainStorage :
    GrainPersistenceTestsRunner,
    IClassFixture<EFCorePersistenceFixture<
        SqlServerGrainStateDbContext,
        byte[],
        SqlServerEFCoreProviderConfiguration>>
{
    public PersistenceGrainTests_EFCoreSqlServerMatrixGrainStorage(
        ITestOutputHelper output,
        EFCorePersistenceFixture<
            SqlServerGrainStateDbContext,
            byte[],
            SqlServerEFCoreProviderConfiguration> fixture)
        : base(output, fixture)
    {
        fixture.EnsurePreconditionsMet();
    }
}

[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestArea("EFCore")]
[TestSuite("Functional")]
[TestProvider(EFCoreTestCategories.MySqlProvider)]
[TestArea("Persistence")]
public sealed class PersistenceGrainTests_EFCoreMySqlGrainStorage :
    GrainPersistenceTestsRunner,
    IClassFixture<EFCorePersistenceFixture<
        MySqlGrainStateDbContext,
        Guid,
        MySqlEFCoreProviderConfiguration>>
{
    public PersistenceGrainTests_EFCoreMySqlGrainStorage(
        ITestOutputHelper output,
        EFCorePersistenceFixture<
            MySqlGrainStateDbContext,
            Guid,
            MySqlEFCoreProviderConfiguration> fixture)
        : base(output, fixture)
    {
        fixture.EnsurePreconditionsMet();
    }
}

[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestArea("EFCore")]
[TestSuite("Functional")]
[TestProvider(EFCoreTestCategories.PostgreSqlProvider)]
[TestArea("Persistence")]
public sealed class PersistenceGrainTests_EFCorePostgreSqlGrainStorage :
    GrainPersistenceTestsRunner,
    IClassFixture<EFCorePersistenceFixture<
        PostgreSqlGrainStateDbContext,
        Guid,
        PostgreSqlEFCoreProviderConfiguration>>
{
    public PersistenceGrainTests_EFCorePostgreSqlGrainStorage(
        ITestOutputHelper output,
        EFCorePersistenceFixture<
            PostgreSqlGrainStateDbContext,
            Guid,
            PostgreSqlEFCoreProviderConfiguration> fixture)
        : base(output, fixture)
    {
        fixture.EnsurePreconditionsMet();
    }
}
