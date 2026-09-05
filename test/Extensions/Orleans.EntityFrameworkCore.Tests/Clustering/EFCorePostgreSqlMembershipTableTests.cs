using Orleans.Clustering.EntityFrameworkCore.PostgreSQL.Data;
using Orleans.EntityFrameworkCore.Tests.Infrastructure;
using TestExtensions;
using UnitTests;

namespace Orleans.EntityFrameworkCore.Tests.Clustering;

[TestCategory("Membership")]
[TestCategory("EFCore")]
[TestCategory(EFCoreTestCategories.PostgreSql)]
[TestCategory(EFCoreTestCategories.PostgreSqlProvider)]
[TestCategory(EFCoreTestCategories.Functional)]
public sealed class EFCorePostgreSqlMembershipTableTests :
    EFCoreMembershipTableTestsBase<PostgreSqlClusterDbContext, Guid>
{
    public EFCorePostgreSqlMembershipTableTests(
        ConnectionStringFixture fixture,
        TestEnvironmentFixture environment)
        : base(fixture, environment)
    {
    }

    protected override EFCoreTestDatabase Database => EFCoreTestDatabase.PostgreSql;

    protected override IEFClusterETagConverter<Guid> CreateETagConverter() =>
        new GuidClusterETagConverter();
}
