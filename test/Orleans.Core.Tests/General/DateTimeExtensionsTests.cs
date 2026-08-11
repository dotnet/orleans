using Orleans.Internal;
using Xunit;

namespace UnitTests.UtilsTests;

public class DateTimeExtensionsTests
{
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Theory, TestCategory("BVT")]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    public void AddClamped_PreservesDateTimeKind(DateTimeKind kind)
    {
        var value = new DateTime(DateTime.MaxValue.Ticks - 1, kind);

        var result = value.AddClamped(TimeSpan.FromTicks(2));

        Assert.Equal(DateTime.MaxValue.Ticks, result.Ticks);
        Assert.Equal(kind, result.Kind);
    }
}
