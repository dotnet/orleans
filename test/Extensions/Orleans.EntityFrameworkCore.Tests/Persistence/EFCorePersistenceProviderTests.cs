using Orleans.EntityFrameworkCore.Tests.Infrastructure;
using Orleans.Persistence.EntityFrameworkCore.MySql.Data;
using Orleans.Persistence.EntityFrameworkCore.PostgreSQL.Data;
using Orleans.Persistence.EntityFrameworkCore.SqlServer.Data;
using TestExtensions;
using Xunit.Abstractions;

namespace Orleans.EntityFrameworkCore.Tests.Persistence;

[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestCategory("Persistence")]
[TestCategory("EFCore")]
[TestCategory(EFCoreTestCategories.SqlServer)]
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
[TestCategory("Persistence")]
[TestCategory("EFCore")]
[TestCategory(EFCoreTestCategories.MySql)]
[TestCategory(EFCoreTestCategories.MySqlProvider)]
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
[TestCategory("Persistence")]
[TestCategory("EFCore")]
[TestCategory(EFCoreTestCategories.PostgreSql)]
[TestCategory(EFCoreTestCategories.PostgreSqlProvider)]
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
