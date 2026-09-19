using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Serialization;
using TestExtensions;
using Xunit;

namespace NonSilo.Tests.Membership;

[TestCategory("BVT"), TestCategory("Membership")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public class MembershipTableWriteResultSerializationTests
{
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    public void WriteResult_GeneratedSerializer_RoundTripsAllValidStates(bool useDefault, bool succeeded, bool includeReceipt)
    {
        var services = new ServiceCollection();
        services.AddSerializer(builder => builder.AddAssembly(typeof(IMembershipTable).Assembly));
        using var serviceProvider = services.BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var receipt = includeReceipt
            ? new MembershipTableWriteReceipt(new TableVersion(73, "W/\"table:\u8868/\u03B1==\""), "\"row:\u884C/\u03B2+=\"")
            : null;
        var original = useDefault ? default : new MembershipTableWriteResult(succeeded, receipt);

        var bytes = serializer.SerializeToArray(original);
        var result = serializer.Deserialize<MembershipTableWriteResult>(bytes);

        Assert.Equal(succeeded, result.Succeeded);
        if (includeReceipt)
        {
            var actualReceipt = Assert.IsType<MembershipTableWriteReceipt>(result.Receipt);
            Assert.NotSame(receipt, actualReceipt);
            Assert.Equal(73, actualReceipt.Version.Version);
            Assert.Equal("W/\"table:\u8868/\u03B1==\"", actualReceipt.Version.VersionEtag);
            Assert.Equal("\"row:\u884C/\u03B2+=\"", actualReceipt.RowETag);
        }
        else
        {
            Assert.Null(result.Receipt);
        }
    }

    [Theory]
    [InlineData(37, "table-etag/0042", "row-etag/0091")]
    [InlineData(0, "", "row/nonempty")]
    [InlineData(71, "table/nonempty", "")]
    [InlineData(105, " \t", "\r\n ")]
    [InlineData(int.MaxValue, "W/\"table:\u8868/\u03B1==\"", "\"row:\u884C/\u03B2+=\"")]
    public void Receipt_GeneratedSerializer_RoundTripsDistinctOpaqueTags(int versionNumber, string tableETag, string rowETag)
    {
        var services = new ServiceCollection();
        services.AddSerializer(builder => builder.AddAssembly(typeof(IMembershipTable).Assembly));
        using var serviceProvider = services.BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var original = new MembershipTableWriteReceipt(new TableVersion(versionNumber, tableETag), rowETag);

        var bytes = serializer.SerializeToArray(original);
        var result = serializer.Deserialize<MembershipTableWriteReceipt>(bytes);

        Assert.NotNull(result);
        Assert.NotSame(original, result);
        Assert.Equal(versionNumber, result.Version.Version);
        Assert.Equal(tableETag, result.Version.VersionEtag);
        Assert.Equal(rowETag, result.RowETag);
    }
}
