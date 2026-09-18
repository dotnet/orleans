using Orleans.EntityFrameworkCore.Tests.Infrastructure;
using Orleans.GrainDirectory.EntityFrameworkCore.PostgreSQL.Data;
using TestExtensions;

namespace Orleans.EntityFrameworkCore.Tests.GrainDirectory;

[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestArea("EFCore")]
[TestSuite("Functional")]
[TestProvider(EFCoreTestCategories.PostgreSqlProvider)]
[TestArea("GrainDirectory")]
public sealed class EFCorePostgreSqlGrainDirectoryTests :
    EFCoreGrainDirectoryTestsBase<PostgreSqlGrainDirectoryDbContext, Guid>
{
    public EFCorePostgreSqlGrainDirectoryTests(ITestOutputHelper testOutput)
        : base(testOutput)
    {
    }

    protected override EFCoreTestDatabase Database => EFCoreTestDatabase.PostgreSql;

    protected override IEFGrainDirectoryETagConverter<Guid> CreateETagConverter() =>
        new GuidGrainDirectoryETagConverter();
}
