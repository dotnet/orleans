using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Orleans.Runtime;
using Xunit;

namespace Tester.Diagnostics;

public class ReminderInstrumentsTests
{
    [Fact, TestCategory("BVT")]
    public void ReminderInstruments_RecordsMetricsUsingMeterFactory()
    {
        var services = new ServiceCollection();
        services.AddMetrics();

        using var serviceProvider = services.BuildServiceProvider();
        var meterFactory = serviceProvider.GetRequiredService<IMeterFactory>();
        var instruments = new ReminderInstruments(new OrleansInstruments(meterFactory));
        using var activeRemindersCollector = new MetricCollector<int>(meterFactory, "Microsoft.Orleans", InstrumentNames.REMINDERS_NUMBER_ACTIVE_REMINDERS);
        using var tardinessCollector = new MetricCollector<double>(meterFactory, "Microsoft.Orleans", InstrumentNames.REMINDERS_TARDINESS);
        using var ticksDeliveredCollector = new MetricCollector<int>(meterFactory, "Microsoft.Orleans", InstrumentNames.REMINDERS_COUNTERS_TICKS_DELIVERED);

        instruments.RegisterActiveRemindersObserve(() => 3);
        instruments.OnTardiness(TimeSpan.FromSeconds(4));
        instruments.OnTickDelivered();

        activeRemindersCollector.RecordObservableInstruments();

        Assert.Equal(3, Assert.Single(activeRemindersCollector.GetMeasurementSnapshot()).Value);
        Assert.Equal(4, Assert.Single(tardinessCollector.GetMeasurementSnapshot()).Value);
        Assert.Equal(1, Assert.Single(ticksDeliveredCollector.GetMeasurementSnapshot()).Value);
    }
}
