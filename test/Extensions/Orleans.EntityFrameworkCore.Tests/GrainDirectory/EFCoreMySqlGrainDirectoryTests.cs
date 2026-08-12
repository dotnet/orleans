using Orleans.EntityFrameworkCore.Tests.Infrastructure;
using Orleans.GrainDirectory.EntityFrameworkCore.MySql.Data;
using TestExtensions;
using Xunit.Abstractions;

namespace Orleans.EntityFrameworkCore.Tests.GrainDirectory;

[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestCategory("GrainDirectory")]
[TestCategory("EFCore")]
[TestCategory(EFCoreTestCategories.MySql)]
[TestCategory(EFCoreTestCategories.MySqlProvider)]
[TestCategory(EFCoreTestCategories.Functional)]
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
