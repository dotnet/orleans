using System.Net;
using Orleans.Runtime;
using TestExtensions;
using Xunit;

namespace UnitTests.Messaging;

public class MessageTests
{
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void AddToCacheInvalidationHeader_LimitsHeaderLength()
    {
        var message = new Message();

        for (var i = 0; i < Message.MaxCacheInvalidationHeaderEntries + 1; i++)
        {
            var grainId = GrainId.Create("test", i.ToString());
            message.AddToCacheInvalidationHeader(CreateAddress(grainId, i), CreateAddress(grainId, i + 100));
        }

        var header = message.CacheInvalidationHeader;
        Assert.NotNull(header);
        Assert.Equal(Message.MaxCacheInvalidationHeaderEntries, header.Count);
        Assert.DoesNotContain(header, update => update.GrainId.Equals(GrainId.Create("test", Message.MaxCacheInvalidationHeaderEntries.ToString())));
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void AddToCacheInvalidationHeader_ComposesUpdatesForTheSameGrain()
    {
        var message = new Message();
        var grainId = GrainId.Create("test", "duplicate");
        var other = GrainId.Create("test", "other");
        var originalAddress = CreateAddress(grainId, 1);
        var otherUpdate = (Invalid: CreateAddress(other, 5), Valid: CreateAddress(other, 6));
        var latestAddress = CreateAddress(grainId, 4);

        // The message moves from its original address to a second, and then to a third. Callers which cached the original address cache the third.
        message.AddToCacheInvalidationHeader(originalAddress, CreateAddress(grainId, 2));
        message.AddToCacheInvalidationHeader(otherUpdate.Invalid, otherUpdate.Valid);
        message.AddToCacheInvalidationHeader(CreateAddress(grainId, 2), latestAddress);

        // An invalidation without a new address keeps the latest known address.
        message.AddToCacheInvalidationHeader(CreateAddress(grainId, 3), validAddress: null);

        var header = message.CacheInvalidationHeader;
        Assert.NotNull(header);
        Assert.Equal(2, header.Count);
        Assert.Equal(grainId, header[0].GrainId);
        Assert.Equal(originalAddress, header[0].InvalidGrainAddress);
        Assert.Equal(latestAddress, header[0].ValidGrainAddress);
        Assert.Equal(otherUpdate.Invalid, header[1].InvalidGrainAddress);
        Assert.Equal(otherUpdate.Valid, header[1].ValidGrainAddress);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void AddToCacheInvalidationHeader_ComposesUpdatesWhenTheHeaderIsFull()
    {
        var message = new Message();
        var first = GrainId.Create("test", "0");
        var original = CreateAddress(first, 0);
        message.AddToCacheInvalidationHeader(original, CreateAddress(first, 100));
        for (var i = 1; i < Message.MaxCacheInvalidationHeaderEntries; i++)
        {
            var grainId = GrainId.Create("test", i.ToString());
            message.AddToCacheInvalidationHeader(CreateAddress(grainId, i), CreateAddress(grainId, i + 100));
        }

        var latest = CreateAddress(first, 200);
        message.AddToCacheInvalidationHeader(CreateAddress(first, 100), latest);

        var header = message.CacheInvalidationHeader;
        Assert.NotNull(header);
        Assert.Equal(Message.MaxCacheInvalidationHeaderEntries, header.Count);
        Assert.Equal(original, header[0].InvalidGrainAddress);
        Assert.Equal(latest, header[0].ValidGrainAddress);
    }

    private static GrainAddress CreateAddress(GrainId grainId, int offset) => new()
    {
        GrainId = grainId,
        ActivationId = ActivationId.NewId(),
        SiloAddress = SiloAddress.New(IPAddress.Loopback, 10_000 + offset, offset + 1)
    };
}
