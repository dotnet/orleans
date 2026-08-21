using Orleans.Tests.SqlUtils;

namespace UnitTests.General;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Clustering")]
public sealed class OracleCommandInterceptorTests
{
    [Theory]
    [InlineData("PayloadJson")]
    [InlineData("PayloadXml")]
    public void IsClobParameter_ReturnsTrueForLobParameters(string parameterName)
    {
        Assert.True(OracleCommandInterceptor.IsClobParameter(parameterName));
    }

    [Fact]
    public void IsClobParameter_ReturnsFalseForRegularStringParameters()
    {
        Assert.False(OracleCommandInterceptor.IsClobParameter("SiloName"));
    }

    [Fact]
    public void IsNClobParameter_ReturnsTrueForMembershipMetadata()
    {
        Assert.True(OracleCommandInterceptor.IsNClobParameter("MetadataJson"));
        Assert.False(OracleCommandInterceptor.IsClobParameter("MetadataJson"));
    }
}
