using System;
using System.Runtime.Serialization;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;
using Xunit;

namespace UnitTests.OrleansRuntime.Streams;

public class QueueCacheMissExceptionTests
{
    [Fact]
    public void ConstructorRejectsNullRequestedToken()
    {
        Assert.Throws<ArgumentNullException>(
            "requested",
            () => new QueueCacheMissException(null!, new EventSequenceToken(1), new EventSequenceToken(2)));
    }

    [Fact]
    public void ConstructorRejectsNullLowToken()
    {
        Assert.Throws<ArgumentNullException>(
            "low",
            () => new QueueCacheMissException(new EventSequenceToken(0), null!, new EventSequenceToken(2)));
    }

    [Fact]
    public void ConstructorRejectsNullHighToken()
    {
        Assert.Throws<ArgumentNullException>(
            "high",
            () => new QueueCacheMissException(new EventSequenceToken(0), new EventSequenceToken(1), null!));
    }

    [Fact]
    public void GetObjectDataRejectsNullAndPreservesLegacyEntries()
    {
        var exception = new QueueCacheMissException("requested", "low", "high");

#pragma warning disable CS0618, SYSLIB0050, SYSLIB0051 // Exercise the existing ISerializable metadata contract, not formatter serialization.
        Assert.Throws<ArgumentNullException>("info", () => exception.GetObjectData(null!, default));
        var info = new SerializationInfo(typeof(QueueCacheMissException), new FormatterConverter());
        exception.GetObjectData(info, default);
#pragma warning restore CS0618, SYSLIB0050, SYSLIB0051

        Assert.Equal("requested", info.GetString("Requested"));
        Assert.Equal("low", info.GetString("Low"));
        Assert.Equal("high", info.GetString("High"));
    }
}
