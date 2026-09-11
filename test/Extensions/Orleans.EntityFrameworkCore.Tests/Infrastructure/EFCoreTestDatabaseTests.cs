using TestExtensions;

namespace Orleans.EntityFrameworkCore.Tests.Infrastructure;

[TestArea("EFCore")]
[TestProvider("None")]
[TestSuite("BVT")]
public sealed class EFCoreTestDatabaseTests
{
    [Fact]
    public void CreateDatabaseName_PreservesUniqueSuffixWhenTruncated()
    {
        var first = EFCoreTestDatabase.PostgreSql.CreateDatabaseName(
            "persistence_direct",
            $"{new string('x', 80)}_net8");
        var second = EFCoreTestDatabase.PostgreSql.CreateDatabaseName(
            "persistence_direct",
            $"{new string('x', 80)}_net10");

        Assert.Equal(63, first.Length);
        Assert.Equal(63, second.Length);
        Assert.NotEqual(first, second);
        Assert.Matches("_[0-9a-f]{32}$", first);
        Assert.Matches("_[0-9a-f]{32}$", second);
    }
}
