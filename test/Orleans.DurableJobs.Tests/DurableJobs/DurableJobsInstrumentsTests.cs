using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Orleans.DurableJobs;
using Orleans.Runtime;
using Xunit;

namespace NonSilo.Tests.DurableJobs;

[TestSuite("BVT")]
[TestProvider("None")]
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
        using var retriedCollector = new MetricCollector<long>(meterFactory, "Microsoft.Orleans", "orleans-durablejobs-jobs-retried");
        using var rescheduledCollector = new MetricCollector<long>(meterFactory, "Microsoft.Orleans", "orleans-durablejobs-jobs-rescheduled");
        using var attemptDurationCollector = new MetricCollector<double>(meterFactory, "Microsoft.Orleans", "orleans-durablejobs-job-attempt-duration");
        using var stripeCollector = new MetricCollector<long>(meterFactory, "Microsoft.Orleans", "orleans-durablejobs-stripe-distribution");

        instruments.OnJobScheduled(TimeSpan.FromMilliseconds(12));
        instruments.OnJobRetried(TimeSpan.FromMilliseconds(15));
        instruments.OnJobRescheduled(TimeSpan.FromMilliseconds(18));
        instruments.OnStripeAssigned(3);

        Assert.Equal(1, Assert.Single(scheduledCollector.GetMeasurementSnapshot()).Value);
        Assert.Equal(12, Assert.Single(scheduleDurationCollector.GetMeasurementSnapshot()).Value);
        Assert.Equal(1, Assert.Single(retriedCollector.GetMeasurementSnapshot()).Value);
        Assert.Equal(1, Assert.Single(rescheduledCollector.GetMeasurementSnapshot()).Value);
        var attemptDurations = attemptDurationCollector.GetMeasurementSnapshot();
        Assert.Equal(15, Assert.Single(attemptDurations, static measurement => measurement.Tags["status"] is "retried").Value);
        Assert.Equal(18, Assert.Single(attemptDurations, static measurement => measurement.Tags["status"] is "rescheduled").Value);
        Assert.Equal(1, Assert.Single(stripeCollector.GetMeasurementSnapshot()).Value);
    }

    [Fact]
    public void DurableJobsInstruments_DistinguishesTaskRequestOperationAndAttemptCancellation()
    {
        var services = new ServiceCollection();
        services.AddMetrics();

        using var serviceProvider = services.BuildServiceProvider();
        var meterFactory = serviceProvider.GetRequiredService<IMeterFactory>();
        var instruments = new DurableJobsInstruments(new OrleansInstruments(meterFactory));
        using var taskCancellationRequests = new MetricCollector<long>(
            meterFactory,
            "Microsoft.Orleans",
            "orleans-durablejobs-job-cancellation-requests");
        using var cancellationRequestCalls = new MetricCollector<long>(
            meterFactory,
            "Microsoft.Orleans",
            "orleans-durablejobs-cancel-job-calls");
        using var handlerExecutions = new MetricCollector<long>(
            meterFactory,
            "Microsoft.Orleans",
            "orleans-durablejobs-handler-executions");
        using var shardsProcessed = new MetricCollector<long>(
            meterFactory,
            "Microsoft.Orleans",
            "orleans-durablejobs-shards-processed");

        instruments.OnJobCancellationRequested();
        instruments.OnCancelJobCall(TimeSpan.FromMilliseconds(1), cancellationRequested: true);
        instruments.OnCancelJobCallCanceled(TimeSpan.FromMilliseconds(1));
        instruments.OnHandlerExecutionAttemptCanceled(TimeSpan.FromMilliseconds(1));
        instruments.OnShardProcessed(TimeSpan.FromMilliseconds(1), attemptCanceled: true, error: false);

        Assert.Equal(1, Assert.Single(taskCancellationRequests.GetMeasurementSnapshot()).Value);
        var requestCalls = cancellationRequestCalls.GetMeasurementSnapshot();
        Assert.Equal(2, requestCalls.Count);
        Assert.Equal(
            1,
            Assert.Single(requestCalls, static measurement => measurement.Tags["status"] is "cancellation_requested").Value);
        Assert.Equal(
            1,
            Assert.Single(requestCalls, static measurement => measurement.Tags["status"] is "operation_canceled").Value);
        Assert.Equal(
            "attempt_canceled",
            Assert.Single(handlerExecutions.GetMeasurementSnapshot()).Tags["status"]);
        Assert.Equal(
            "attempt_canceled",
            Assert.Single(shardsProcessed.GetMeasurementSnapshot()).Tags["status"]);
    }
}
