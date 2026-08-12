using Orleans.Clustering.EntityFrameworkCore.MySql.Data;
using Orleans.EntityFrameworkCore.Tests.Infrastructure;
using TestExtensions;
using UnitTests;

namespace Orleans.EntityFrameworkCore.Tests.Clustering;

[TestCategory("Membership")]
[TestCategory("EFCore")]
[TestCategory(EFCoreTestCategories.MySql)]
[TestCategory(EFCoreTestCategories.MySqlProvider)]
[TestCategory(EFCoreTestCategories.Functional)]
[TestSuite("Functional")]
[TestProvider(EFCoreTestCategories.MySqlProvider)]
[TestArea("Membership")]
public sealed class EFCoreMySqlMembershipTableTests :
    EFCoreMembershipTableTestsBase<MySqlClusterDbContext, Guid>
{
    public EFCoreMySqlMembershipTableTests(
        ConnectionStringFixture fixture,
        TestEnvironmentFixture environment)
        : base(fixture, environment)
    {
    }

    protected override EFCoreTestDatabase Database => EFCoreTestDatabase.MySql;

    protected override IEFClusterETagConverter<Guid> CreateETagConverter() =>
        new GuidClusterETagConverter();
}
