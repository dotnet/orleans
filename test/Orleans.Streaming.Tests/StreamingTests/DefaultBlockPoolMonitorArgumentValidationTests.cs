using System;
using System.Collections.Generic;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Xunit;

namespace UnitTests.StreamingTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Streaming")]
[TestCategory("BVT"), TestCategory("Streaming")]
public class DefaultBlockPoolMonitorArgumentValidationTests
{
    [Fact]
    public void ConstructorsRejectNullRequiredInputs()
    {
        Assert.Throws<ArgumentNullException>(
            "dimensions",
            () => new DefaultBlockPoolMonitor(null!, null!));

        var dimensions = new BlockPoolMonitorDimensions("pool");
        Assert.Throws<ArgumentNullException>(
            "instruments",
            () => new DefaultBlockPoolMonitor(dimensions, null!));

        Assert.Throws<ArgumentNullException>(
            "instruments",
            () => new ExposedDefaultBlockPoolMonitor(Array.Empty<KeyValuePair<string, object>>(), null!));
    }

    private sealed class ExposedDefaultBlockPoolMonitor(
        KeyValuePair<string, object>[] dimensions,
        OrleansInstruments instruments) : DefaultBlockPoolMonitor(dimensions, instruments);
}
