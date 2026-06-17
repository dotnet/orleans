using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Orleans.Messaging;
using Orleans.Runtime;
using Xunit;

namespace Tester.Diagnostics;

public class MessagingInstrumentsTests
{
    [Fact, TestCategory("BVT")]
    public void MessagingInstruments_RecordsMetricsUsingMeterFactory()
    {
        var services = new ServiceCollection();
        services.AddMetrics();

        using var serviceProvider = services.BuildServiceProvider();
        var meterFactory = serviceProvider.GetRequiredService<IMeterFactory>();
        var instruments = new MessagingInstruments(new OrleansInstruments(meterFactory));
        using var rejectedCollector = new MetricCollector<int>(meterFactory, "Microsoft.Orleans", InstrumentNames.MESSAGING_REJECTED);
        using var expiredCollector = new MetricCollector<int>(meterFactory, "Microsoft.Orleans", InstrumentNames.MESSAGING_EXPIRED);
        using var sentSizeCollector = new MetricCollector<int>(meterFactory, "Microsoft.Orleans", InstrumentNames.MESSAGING_SENT_MESSAGES_SIZE);
        using var receivedSizeCollector = new MetricCollector<int>(meterFactory, "Microsoft.Orleans", InstrumentNames.MESSAGING_RECEIVED_MESSAGES_SIZE);
        using var sentHeaderCollector = new MetricCollector<long>(meterFactory, "Microsoft.Orleans", InstrumentNames.MESSAGING_SENT_BYTES_HEADER);
        using var receivedHeaderCollector = new MetricCollector<long>(meterFactory, "Microsoft.Orleans", InstrumentNames.MESSAGING_RECEIVED_BYTES_HEADER);

        var message = new Message { Direction = Message.Directions.Request };

        instruments.OnRejectedMessage(message);
        instruments.OnMessageExpired(MessagingInstruments.Phase.Send);
        instruments.OnMessageSend(message, numTotalBytes: 12, headerBytes: 5, ConnectionDirection.SiloToSilo);
        instruments.OnMessageReceive(message, numTotalBytes: 17, headerBytes: 7, ConnectionDirection.SiloToSilo);

        sentHeaderCollector.RecordObservableInstruments();
        receivedHeaderCollector.RecordObservableInstruments();

        Assert.Equal(1, Assert.Single(rejectedCollector.GetMeasurementSnapshot()).Value);
        Assert.Equal(1, Assert.Single(expiredCollector.GetMeasurementSnapshot()).Value);
        Assert.Equal(12, Assert.Single(sentSizeCollector.GetMeasurementSnapshot()).Value);
        Assert.Equal(17, Assert.Single(receivedSizeCollector.GetMeasurementSnapshot()).Value);
        Assert.Equal(5, Assert.Single(sentHeaderCollector.GetMeasurementSnapshot()).Value);
        Assert.Equal(7, Assert.Single(receivedHeaderCollector.GetMeasurementSnapshot()).Value);
    }
}
