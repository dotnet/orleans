using Orleans.Clustering.EntityFrameworkCore.SqlServer;
using Orleans.Clustering.EntityFrameworkCore.SqlServer.Data;
using Orleans.EntityFrameworkCore.Tests.Infrastructure;
using TestExtensions;
using UnitTests;

namespace Orleans.EntityFrameworkCore.Tests.Clustering;

[TestCategory("Membership")]
[TestCategory("EFCore")]
[TestCategory(EFCoreTestCategories.SqlServer)]
[TestCategory(EFCoreTestCategories.Functional)]
[TestSuite("Functional")]
[TestProvider(EFCoreTestCategories.SqlServer)]
[TestArea("Membership")]
public sealed class EFCoreSqlServerMembershipTableTests :
    EFCoreMembershipTableTestsBase<SqlServerClusterDbContext, byte[]>
{
    public EFCoreSqlServerMembershipTableTests(
        ConnectionStringFixture fixture,
        TestEnvironmentFixture environment)
        : base(fixture, environment)
    {
    }

    protected override EFCoreTestDatabase Database => EFCoreTestDatabase.SqlServer;

    protected override IEFClusterETagConverter<byte[]> CreateETagConverter() =>
        new SqlServerClusterETagConverter();
}
