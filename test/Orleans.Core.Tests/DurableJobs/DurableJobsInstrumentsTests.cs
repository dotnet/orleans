using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Orleans.DurableJobs;
using Orleans.Runtime;
using Xunit;

namespace NonSilo.Tests.DurableJobs;

[TestCategory("BVT")]
public class DurableJobsInstrumentsTests
{
    [Fact]
    public void DurableJobsInstruments_RecordsMetricsUsingMeterFactory()
    {
        var services = new ServiceCollection();
        services.AddMetrics();

        using var serviceProvider = services.BuildServiceProvider();
        var meterFactory = serviceProvider.GetRequiredService<IMeterFactory>();
        var instruments = new DurableJobsInstruments(new OrleansInstruments(meterFactory));
        using var scheduledCollector = new MetricCollector<long>(meterFactory, "Microsoft.Orleans", "orleans-durablejobs-jobs-scheduled");
        using var scheduleDurationCollector = new MetricCollector<double>(meterFactory, "Microsoft.Orleans", "orleans-durablejobs-job-schedule-duration");
        using var stripeCollector = new MetricCollector<long>(meterFactory, "Microsoft.Orleans", "orleans-durablejobs-stripe-distribution");

        instruments.OnJobScheduled(TimeSpan.FromMilliseconds(12));
        instruments.OnStripeAssigned(3);

        Assert.Equal(1, Assert.Single(scheduledCollector.GetMeasurementSnapshot()).Value);
        Assert.Equal(12, Assert.Single(scheduleDurationCollector.GetMeasurementSnapshot()).Value);
        Assert.Equal(1, Assert.Single(stripeCollector.GetMeasurementSnapshot()).Value);
    }
}
