using Orleans.EntityFrameworkCore.Tests.Infrastructure;
using Orleans.GrainDirectory.EntityFrameworkCore.PostgreSQL.Data;
using TestExtensions;
using Xunit.Abstractions;

namespace Orleans.EntityFrameworkCore.Tests.GrainDirectory;

[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestCategory("GrainDirectory")]
[TestCategory("EFCore")]
[TestCategory(EFCoreTestCategories.PostgreSql)]
[TestCategory(EFCoreTestCategories.PostgreSqlProvider)]
[TestCategory(EFCoreTestCategories.Functional)]
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
