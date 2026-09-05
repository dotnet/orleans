using Orleans.EntityFrameworkCore.Tests.Infrastructure;
using Orleans.GrainDirectory;
using Orleans.GrainDirectory.EntityFrameworkCore.SqlServer.Data;
using TestExtensions;
using Xunit.Abstractions;

namespace Orleans.EntityFrameworkCore.Tests.GrainDirectory;

[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestCategory("GrainDirectory")]
[TestCategory("EFCore")]
[TestCategory(EFCoreTestCategories.SqlServer)]
[TestCategory(EFCoreTestCategories.Functional)]
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
