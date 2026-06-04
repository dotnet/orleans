using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Orleans.Runtime;
using Xunit;

namespace Tester.Diagnostics;

public class MessagingProcessingInstrumentsTests
{
    [Fact, TestCategory("BVT")]
    public void MessagingProcessingInstruments_RecordsMetricsUsingMeterFactory()
    {
        var services = new ServiceCollection();
        services.AddMetrics();

        using var serviceProvider = services.BuildServiceProvider();
        var meterFactory = serviceProvider.GetRequiredService<IMeterFactory>();
        var instruments = new MessagingProcessingInstruments(new OrleansInstruments(meterFactory));
        using var forwardedCollector = new MetricCollector<long>(meterFactory, "Microsoft.Orleans", InstrumentNames.MESSAGING_DISPATCHER_FORWARDED);
        using var receivedCollector = new MetricCollector<long>(meterFactory, "Microsoft.Orleans", InstrumentNames.MESSAGING_IMA_RECEIVED);
        using var activationDataCollector = new MetricCollector<long>(meterFactory, "Microsoft.Orleans", InstrumentNames.MESSAGING_PROCESSING_ACTIVATION_DATA_ALL);

        var message = new Message { Direction = Message.Directions.Request };

        instruments.RegisterActivationDataAllObserve(() => 9);
        instruments.OnDispatcherMessageForwared(message);
        instruments.OnImaMessageReceived(message);

        forwardedCollector.RecordObservableInstruments();
        receivedCollector.RecordObservableInstruments();
        activationDataCollector.RecordObservableInstruments();

        Assert.Equal(1, Assert.Single(forwardedCollector.GetMeasurementSnapshot()).Value);
        Assert.Equal(1, Assert.Single(receivedCollector.GetMeasurementSnapshot()).Value);
        Assert.Equal(9, Assert.Single(activationDataCollector.GetMeasurementSnapshot()).Value);
    }
}
