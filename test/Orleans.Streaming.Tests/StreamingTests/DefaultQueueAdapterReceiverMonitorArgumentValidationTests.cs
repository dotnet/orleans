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
public class DefaultQueueAdapterReceiverMonitorArgumentValidationTests
{
    [Fact]
    public void ConstructorsRejectNullRequiredInputs()
    {
        Assert.Throws<ArgumentNullException>(
            "dimensions",
            () => new DefaultQueueAdapterReceiverMonitor(null!, null!));

        var dimensions = new ReceiverMonitorDimensions("queue");
        Assert.Throws<ArgumentNullException>(
            "instruments",
            () => new DefaultQueueAdapterReceiverMonitor(dimensions, null!));

        Assert.Throws<ArgumentNullException>(
            "instruments",
            () => new ExposedDefaultQueueAdapterReceiverMonitor(Array.Empty<KeyValuePair<string, object>>(), null!));
    }

    private sealed class ExposedDefaultQueueAdapterReceiverMonitor(
        KeyValuePair<string, object>[] dimensions,
        OrleansInstruments instruments) : DefaultQueueAdapterReceiverMonitor(dimensions, instruments);
}
