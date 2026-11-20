using Orleans.DurableTasks;
using Xunit;

namespace Orleans.DurableTasks.Abstractions.Tests;

/// <summary>Tests the argument contract of <see cref="DurableTask.GetVersion(string, int, int)"/>.</summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
[TestCategory("BVT")]
public class DurableTaskVersionTests
{
    [Fact]
    public void OutsideADurableTaskTheNewestVersionIsReturned()
    {
        Assert.Equal(2, DurableTask.GetVersion("change", 0, 2));
        Assert.Equal(3, DurableTask.GetVersion("change", 3, 3));
    }

    [Theory]
    [InlineData("")]
    [InlineData("$reserved")]
    [InlineData("change$2")]
    public void NamesAreNonEmptyAndExcludeTheGeneratedIdentifierMarker(string name)
    {
        Assert.Throws<ArgumentException>(() => DurableTask.GetVersion(name, 0, 1));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(2, 1)]
    public void SupportedRangeStartsAtZeroAndIsOrdered(int minSupported, int maxSupported)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DurableTask.GetVersion("change", minSupported, maxSupported));
    }

    [Fact]
    public void NullNameIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => DurableTask.GetVersion(null!, 0, 1));
    }

    [Fact]
    public void DescriptorOutsideADurableTaskReturnsItsNewestVersion()
    {
        var change = new DurableVersion("change", minSupported: 1, maxSupported: 3);

        Assert.Equal(3, change.Get());
        Assert.True(change.IsAtLeast(3));
        Assert.False(change.IsAtLeast(4));
        Assert.Equal("change [1..3]", change.ToString());
    }

    [Theory]
    [InlineData("", 0, 1)]
    [InlineData("$reserved", 0, 1)]
    public void DescriptorNamesAreValidated(string name, int minSupported, int maxSupported)
    {
        Assert.Throws<ArgumentException>(() => new DurableVersion(name, minSupported, maxSupported));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(2, 1)]
    public void DescriptorRangesAreValidated(int minSupported, int maxSupported)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableVersion("change", minSupported, maxSupported));
    }
}
