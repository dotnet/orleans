using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace DurableJobsJournaling.Silo;

public sealed class GrainRequestMetricsFilter : IIncomingGrainCallFilter
{
    private static readonly Meter Meter = new("Orleans.Playground.DurableJobsJournaling");
    private static readonly Counter<long> Requests = Meter.CreateCounter<long>("playground.grain.requests");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("playground.grain.request.duration", unit: "ms");

    public async Task Invoke(IIncomingGrainCallContext context)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var grainType = context.TargetId.Type.ToString() ?? "unknown";

        try
        {
            await context.Invoke();
            Record(grainType, "success", startedAt);
        }
        catch
        {
            Record(grainType, "error", startedAt);
            throw;
        }
    }

    private static void Record(string grainType, string status, long startedAt)
    {
        var elapsedMilliseconds = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        var tags = new TagList
        {
            { "grain_type", grainType },
            { "status", status }
        };

        Requests.Add(1, tags);
        Duration.Record(elapsedMilliseconds, tags);
    }
}
