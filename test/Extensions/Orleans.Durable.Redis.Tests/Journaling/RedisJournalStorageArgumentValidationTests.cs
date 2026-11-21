using Orleans.Durable.Journaling;
using Xunit;

namespace Tester.Redis.Journaling;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class RedisJournalStorageArgumentValidationTests
{
    [Fact]
    public void AddRedisJournalStorage_NullBuilder_Throws()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => RedisJournalStorageHostingExtensions.AddRedisJournalStorage(null!));

        Assert.Equal("builder", exception.ParamName);
    }
}
