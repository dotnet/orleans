using Orleans.EntityFrameworkCore.Tests.Infrastructure;
using Orleans.GrainDirectory;
using Orleans.GrainDirectory.EntityFrameworkCore.SqlServer.Data;
using TestExtensions;

namespace Orleans.EntityFrameworkCore.Tests.GrainDirectory;

[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestArea("EFCore")]
[TestSuite("Functional")]
[TestProvider(EFCoreTestCategories.SqlServer)]
[TestArea("GrainDirectory")]
public sealed class EFCoreSqlServerGrainDirectoryMatrixTests :
    EFCoreGrainDirectoryTestsBase<SqlServerGrainDirectoryDbContext, byte[]>
{
    public EFCoreSqlServerGrainDirectoryMatrixTests(ITestOutputHelper testOutput)
        : base(testOutput)
    {
    }

    protected override EFCoreTestDatabase Database => EFCoreTestDatabase.SqlServer;

    protected override IEFGrainDirectoryETagConverter<byte[]> CreateETagConverter() =>
        new SqlServerGrainDirectoryETagConverter();
}
