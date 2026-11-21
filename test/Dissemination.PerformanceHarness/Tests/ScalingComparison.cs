using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Orleans.Dissemination.PerformanceHarness;

internal sealed record ScalingComparison(ScalingPair[] Pairs, int IncompletePairs)
{
    public static ScalingComparison Create(string json)
    {
        var samples = JsonSerializer.Deserialize<ScalingSample[]>(json)
            ?? throw new InvalidOperationException("Missing scaling samples.");
        var pairs = new List<ScalingPair>();
        var incomplete = 0;
        foreach (var group in samples
            .Where(sample => sample.RuntimePath is "CurrentDisabled" or "CurrentEnabledSupported")
            .GroupBy(sample => (sample.Size, sample.Scenario, sample.Repetition)))
        {
            if (group.Count() == 1)
            {
                incomplete++;
                continue;
            }

            var disabled = group.Single(sample => sample.RuntimePath == "CurrentDisabled");
            var enabled = group.Single(sample => sample.RuntimePath == "CurrentEnabledSupported");
            Validate(disabled);
            Validate(enabled);
            if (disabled.Iterations != enabled.Iterations
                || disabled.Workload != enabled.Workload
                || disabled.OfferedPublications != enabled.OfferedPublications
                || disabled.Runtime.SourceRevision != enabled.Runtime.SourceRevision
                || disabled.Runtime.Sha256 != enabled.Runtime.Sha256
                || disabled.Environment.GetRawText() != enabled.Environment.GetRawText())
            {
                throw new InvalidOperationException($"Mismatched binary, workload, or environment for {group.Key}.");
            }

            pairs.Add(new(
                disabled.Size, disabled.Scenario, disabled.Repetition, disabled.Iterations, disabled.OfferedPublications,
                disabled.Workload,
                disabled.Runtime.SourceRevision, disabled.Runtime.Sha256, disabled.Environment,
                ScalingCost.From(disabled), ScalingCost.From(enabled)));
        }

        return new(pairs.ToArray(), incomplete);
    }

    private static void Validate(ScalingSample sample)
    {
        if (sample.Size < 3 || sample.LiveSilos != sample.Size || sample.OfferedPublications <= 0
            || sample.Runtime is null || string.IsNullOrEmpty(sample.Runtime.SourceRevision) || string.IsNullOrEmpty(sample.Runtime.Sha256)
            || sample.Environment.ValueKind != JsonValueKind.Object
            || sample.Iterations < 3 || sample.ConvergenceMilliseconds is null
            || sample.TotalRpcs <= 0 || sample.SerializedBytesSent <= 0 || sample.AllocatedBytes <= 0
            || sample.CpuMilliseconds < 0 || sample.ConvergenceMilliseconds.Any(value => !double.IsFinite(value) || value < 0))
        {
            throw new InvalidOperationException($"Incomplete scaling measurement for {sample.RuntimePath}, {sample.Size} silos.");
        }
        if (sample.Workload == "ClosedLoop")
        {
            if (sample.ConvergenceMilliseconds.Length != sample.Iterations || sample.OpenLoop is not null)
            {
                throw new InvalidOperationException("Closed-loop measurements require one convergence latency per round.");
            }
        }
        else if (sample.Workload is "OpenLoopSynchronized" or "OpenLoopStaggered"
            && sample.OpenLoop is { } openLoop)
        {
            if (sample.Scenario != "stable" || sample.Size > 32 || sample.Iterations > 30
                || sample.OfferedPublications != sample.Size * sample.Iterations
                || sample.ConvergenceMilliseconds.Length != 0 || openLoop.ProducerRateHz != 1
                || openLoop.DurationSeconds != sample.Iterations || openLoop.MissedPeriods != 0 || openLoop.Overruns != 0
                || openLoop.FinalLatestLatencyUpperBoundMilliseconds.Length != sample.Size
                || openLoop.FirstObservedAgeUpperBoundMilliseconds.Length == 0
                || openLoop.PublicationMilliseconds.Length != sample.OfferedPublications
                || openLoop.ActualPublicationPeriodsMilliseconds.Length != sample.Size * (sample.Iterations - 1)
                || openLoop.ObservedPublicationPairs < sample.Size * sample.Size || openLoop.UnobservedPublicationPairs < 0
                || openLoop.ObservedPublicationPairs + openLoop.UnobservedPublicationPairs != sample.Size * sample.OfferedPublications
                || !double.IsFinite(openLoop.MaximumStartLatenessMilliseconds)
                || openLoop.MaximumStartLatenessMilliseconds is < 0 or >= 1000
                || openLoop.FinalLatestLatencyUpperBoundMilliseconds.Concat(openLoop.FirstObservedAgeUpperBoundMilliseconds)
                    .Any(value => !double.IsFinite(value) || value < 0 || value > (sample.Iterations + 10) * 1000))
            {
                throw new InvalidOperationException("Incomplete or out-of-bounds 1 Hz open-loop measurement.");
            }
        }
        else
        {
            throw new InvalidOperationException($"Missing or unsupported workload measurement: {sample.Workload}.");
        }
    }

    public string ToMarkdown()
    {
        var result = new StringBuilder();
        result.AppendLine("### Dissemination disabled vs enabled");
        result.AppendLine("Enabled mode explicitly opts into the subsystem and both namespaces, retaining the candidate's production dissemination tuning defaults.");
        result.AppendLine("Each cell is disabled -> enabled. Costs per offered publication include background and control traffic. Single-host, resource-shared measurements; raw totals, environment and per-node data are in the artifacts.");
        result.AppendLine("ClosedLoop latency is per-round convergence. OpenLoop latency is the polling upper bound for each producer's final value reaching all processes; cadence, observed intermediate values and their age upper bounds are in the raw open-loop artifacts.");
        result.AppendLine();
        result.AppendLine("| Silos / scenario / workload / repetition | RPCs / publication | KiB / publication | Allocated KiB / publication | CPU ms / publication | Median ms | P95 ms |");
        result.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        foreach (var pair in Pairs)
        {
            result.AppendLine(FormattableString.Invariant(
                $"| {pair.Size} / {pair.Scenario} / {pair.Workload} / {pair.Repetition} | {pair.Disabled.RpcsPerPublication:F2} -> {pair.Enabled.RpcsPerPublication:F2} | {pair.Disabled.SerializedBytesPerPublication / 1024:F2} -> {pair.Enabled.SerializedBytesPerPublication / 1024:F2} | {pair.Disabled.AllocatedBytesPerPublication / 1024:F2} -> {pair.Enabled.AllocatedBytesPerPublication / 1024:F2} | {pair.Disabled.CpuMillisecondsPerPublication:F2} -> {pair.Enabled.CpuMillisecondsPerPublication:F2} | {pair.Disabled.MedianConvergenceMilliseconds:F2} -> {pair.Enabled.MedianConvergenceMilliseconds:F2} | {pair.Disabled.P95ConvergenceMilliseconds:F2} -> {pair.Enabled.P95ConvergenceMilliseconds:F2} |"));
        }

        result.AppendLine();
        result.AppendLine($"Incomplete pairs: {IncompletePairs.ToString(CultureInfo.InvariantCulture)}.");
        return result.ToString();
    }
}

internal sealed record ScalingSample(
    string RuntimePath, int Size, int LiveSilos, string Scenario, int Repetition, int Iterations, int OfferedPublications,
    ScalingBinary Runtime, JsonElement Environment, long TotalRpcs, double SerializedBytesSent,
    long AllocatedBytes, double CpuMilliseconds, long RetainedManagedBytes, double[] ConvergenceMilliseconds,
    string Workload = "ClosedLoop", OpenLoopSummary? OpenLoop = null);

internal sealed record ScalingBinary(string SourceRevision, string Sha256);

internal sealed record ScalingPair(
    int Size, string Scenario, int Repetition, int Iterations, int OfferedPublications,
    string Workload,
    string SourceRevision, string Sha256, JsonElement Environment, ScalingCost Disabled, ScalingCost Enabled);

internal sealed record ScalingCost(
    long TotalRpcs, double SerializedBytesSent, long AllocatedBytes, double CpuMilliseconds, long RetainedManagedBytes,
    double RpcsPerPublication, double SerializedBytesPerPublication, double AllocatedBytesPerPublication,
    double CpuMillisecondsPerPublication, double MedianConvergenceMilliseconds, double P95ConvergenceMilliseconds)
{
    public static ScalingCost From(ScalingSample sample)
    {
        var ordered = (sample.Workload == "ClosedLoop" ? sample.ConvergenceMilliseconds
            : sample.OpenLoop!.FinalLatestLatencyUpperBoundMilliseconds).Order().ToArray();
        var median = (ordered[(ordered.Length - 1) / 2] + ordered[ordered.Length / 2]) / 2;
        return new(
            sample.TotalRpcs, sample.SerializedBytesSent, sample.AllocatedBytes, sample.CpuMilliseconds, sample.RetainedManagedBytes,
            (double)sample.TotalRpcs / sample.OfferedPublications, sample.SerializedBytesSent / sample.OfferedPublications,
            (double)sample.AllocatedBytes / sample.OfferedPublications, sample.CpuMilliseconds / sample.OfferedPublications,
            median, ordered[(int)Math.Ceiling(0.95 * ordered.Length) - 1]);
    }
}
