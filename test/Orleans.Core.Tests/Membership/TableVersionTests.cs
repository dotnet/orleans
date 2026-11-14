using Orleans;
using TestExtensions;
using Xunit;

namespace NonSilo.Tests.Membership;

[TestCategory("BVT"), TestCategory("Membership")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public class TableVersionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(int.MaxValue - 1)]
    public void Next_IncrementsVersionAndRetainsConcurrencyToken(int version)
    {
        var current = new TableVersion(version, "etag");

        var next = current.Next();

        Assert.Equal(version + 1, next.Version);
        Assert.Equal(current.VersionEtag, next.VersionEtag);
        Assert.Equal(version, current.Version);
    }

    [Fact]
    public void Next_AtVersionLimit_ThrowsOverflowException()
    {
        var current = new TableVersion(int.MaxValue, "etag");

        Assert.Throws<OverflowException>(() => current.Next());

        Assert.Equal(int.MaxValue, current.Version);
        Assert.Equal("etag", current.VersionEtag);
    }
}
