using Orleans.EntityFrameworkCore.Tests.Infrastructure;
using Orleans.Persistence.EntityFrameworkCore.MySql.Data;
using Orleans.Persistence.EntityFrameworkCore.PostgreSQL.Data;
using Orleans.Persistence.EntityFrameworkCore.SqlServer.Data;
using TestExtensions;

namespace Orleans.EntityFrameworkCore.Tests.Persistence;

[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestArea("EFCore")]
[TestSuite("Functional")]
[TestProvider(EFCoreTestCategories.SqlServer)]
[TestArea("Persistence")]
public sealed class EFCoreSqlServerPersistenceProviderTests :
    EFCorePersistenceProviderTestsBase<
        SqlServerGrainStateDbContext,
        byte[],
        SqlServerEFCoreProviderConfiguration>
{
    public EFCoreSqlServerPersistenceProviderTests(ITestOutputHelper testOutput)
        : base(testOutput)
    {
    }
}

[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestArea("EFCore")]
[TestSuite("Functional")]
[TestProvider(EFCoreTestCategories.MySqlProvider)]
[TestArea("Persistence")]
public sealed class EFCoreMySqlPersistenceProviderTests :
    EFCorePersistenceProviderTestsBase<
        MySqlGrainStateDbContext,
        Guid,
        MySqlEFCoreProviderConfiguration>
{
    public EFCoreMySqlPersistenceProviderTests(ITestOutputHelper testOutput)
        : base(testOutput)
    {
    }
}

[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestArea("EFCore")]
[TestSuite("Functional")]
[TestProvider(EFCoreTestCategories.PostgreSqlProvider)]
[TestArea("Persistence")]
public sealed class EFCorePostgreSqlPersistenceProviderTests :
    EFCorePersistenceProviderTestsBase<
        PostgreSqlGrainStateDbContext,
        Guid,
        PostgreSqlEFCoreProviderConfiguration>
{
    public EFCorePostgreSqlPersistenceProviderTests(ITestOutputHelper testOutput)
        : base(testOutput)
    {
    }
}
