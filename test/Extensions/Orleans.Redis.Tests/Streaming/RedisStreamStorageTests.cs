using Orleans.Streaming.Redis;
using Xunit;

namespace Tester.Redis.Streaming;

[TestCategory("Redis"), TestCategory("Streaming")]
public sealed class RedisStreamStorageTests
{
    [Theory]
    [InlineData("1735790000000-3", "1735790000000-4")]
    [InlineData("1735790000000-9223372036854775807", "1735790000001-0")]
    public void RedisStreamStorage_GetNextEntryId_ReturnsInclusiveSuccessor(string currentEntryId, string expectedNextEntryId)
    {
        var nextEntryId = RedisStreamStorage.GetNextEntryId(currentEntryId);

        Assert.Equal(expectedNextEntryId, nextEntryId);
    }

    [Fact]
    public void RedisStreamStorage_GetNextEntryId_ThrowsOnOverflow()
    {
        Assert.Throws<OverflowException>(() => RedisStreamStorage.GetNextEntryId($"{long.MaxValue}-{long.MaxValue}"));
    }
}
