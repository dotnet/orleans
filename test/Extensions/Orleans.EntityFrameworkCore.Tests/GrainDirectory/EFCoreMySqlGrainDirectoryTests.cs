using Orleans.EntityFrameworkCore.Tests.Infrastructure;
using Orleans.GrainDirectory.EntityFrameworkCore.MySql.Data;
using TestExtensions;

namespace Orleans.EntityFrameworkCore.Tests.GrainDirectory;

[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestArea("EFCore")]
[TestSuite("Functional")]
[TestProvider(EFCoreTestCategories.MySqlProvider)]
[TestArea("GrainDirectory")]
public sealed class EFCoreMySqlGrainDirectoryTests :
    EFCoreGrainDirectoryTestsBase<MySqlGrainDirectoryDbContext, Guid>
{
    public EFCoreMySqlGrainDirectoryTests(ITestOutputHelper testOutput)
        : base(testOutput)
    {
    }

    protected override EFCoreTestDatabase Database => EFCoreTestDatabase.MySql;

    protected override IEFGrainDirectoryETagConverter<Guid> CreateETagConverter() =>
        new GuidGrainDirectoryETagConverter();
}
