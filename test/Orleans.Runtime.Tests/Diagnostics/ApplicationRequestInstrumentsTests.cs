using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Orleans.Runtime;
using Xunit;

namespace Tester.Diagnostics;

public class ApplicationRequestInstrumentsTests
{
    [Fact, TestCategory("BVT")]
    public void TimedOutAndCanceledCounters_CarryGrainTypeTag()
    {
        var services = new ServiceCollection();
        services.AddMetrics();

        using var serviceProvider = services.BuildServiceProvider();
        var meterFactory = serviceProvider.GetRequiredService<IMeterFactory>();
        var instruments = new ApplicationRequestInstruments(new OrleansInstruments(meterFactory));
        using var timedOutCollector = new MetricCollector<long>(meterFactory, "Microsoft.Orleans", InstrumentNames.APP_REQUESTS_TIMED_OUT);
        using var canceledCollector = new MetricCollector<long>(meterFactory, "Microsoft.Orleans", InstrumentNames.APP_REQUESTS_CANCELED);

        instruments.OnAppRequestsTimedOut("mygrain");
        instruments.OnAppRequestsCanceled("othergrain");

        var timedOut = Assert.Single(timedOutCollector.GetMeasurementSnapshot());
        Assert.Equal(1, timedOut.Value);
        Assert.Equal("mygrain", Assert.Contains("grain_type", timedOut.Tags));

        var canceled = Assert.Single(canceledCollector.GetMeasurementSnapshot());
        Assert.Equal(1, canceled.Value);
        Assert.Equal("othergrain", Assert.Contains("grain_type", canceled.Tags));
    }
}
