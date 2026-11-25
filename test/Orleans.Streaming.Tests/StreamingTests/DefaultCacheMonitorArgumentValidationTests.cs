using System;
using System.Collections.Generic;
using Orleans.Runtime;
using Orleans.Providers.Streams.Common;
using Xunit;

namespace UnitTests.StreamingTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Streaming")]
[TestCategory("BVT"), TestCategory("Streaming")]
public class DefaultCacheMonitorArgumentValidationTests
{
    [Fact]
    public void ConstructorsRejectNullRequiredInputs()
    {
        Assert.Throws<ArgumentNullException>(
            "dimensions",
            () => new DefaultCacheMonitor(null!, null!));

        var dimensions = new CacheMonitorDimensions("queue", "pool");
        Assert.Throws<ArgumentNullException>(
            "instruments",
            () => new DefaultCacheMonitor(dimensions, null!));

        Assert.Throws<ArgumentNullException>(
            "instruments",
            () => new ExposedDefaultCacheMonitor(Array.Empty<KeyValuePair<string, object>>(), null!));
    }

    private sealed class ExposedDefaultCacheMonitor(
        KeyValuePair<string, object>[] dimensions,
        OrleansInstruments instruments) : DefaultCacheMonitor(dimensions, instruments);
}
