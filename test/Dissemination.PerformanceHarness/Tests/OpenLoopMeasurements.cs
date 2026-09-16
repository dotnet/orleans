namespace Orleans.Dissemination.PerformanceHarness;

internal sealed record OpenLoopSummary(
    int DurationSeconds, int ProducerRateHz, int ObservedPublicationPairs, int UnobservedPublicationPairs,
    int MissedPeriods, int Overruns, double MaximumStartLatenessMilliseconds,
    double[] ActualPublicationPeriodsMilliseconds, double[] PublicationMilliseconds,
    double[] FirstObservedAgeUpperBoundMilliseconds, double[] FinalLatestLatencyUpperBoundMilliseconds);

internal sealed record OpenLoopObservation(int ObserverProcessId, string Source, long Version, DateTimeOffset ObservedAtUtc, string Value);
internal sealed record OpenLoopNodeReport(int ProcessId, string Address, OpenLoopReport Report);

internal sealed class OpenLoopMeasurements(Dictionary<int, string> nodes, Dictionary<string, long> initialVersions, int seconds)
{
    private readonly Dictionary<(int Observer, string Source, long Version), OpenLoopObservation> _observations = [];

    public OpenLoopObservation[] Observations => _observations.Values.ToArray();

    public void Observe(int processId, DateTimeOffset capturedAtUtc, IReadOnlyDictionary<string, long> versions, IReadOnlyDictionary<string, string> load)
    {
        if (!nodes.ContainsKey(processId) || versions.Count != nodes.Count || load.Count != nodes.Count
            || versions.Keys.Any(address => !initialVersions.ContainsKey(address)))
        {
            throw new InvalidOperationException("Open-loop observation has an unexpected process or load inventory.");
        }

        foreach (var (address, version) in versions)
        {
            if (!load.TryGetValue(address, out var value))
            {
                throw new InvalidOperationException($"Missing observed load value for {address}.");
            }
            if (version > initialVersions[address])
            {
                _observations.TryAdd((processId, address, version), new(processId, address, version, capturedAtUtc, value));
            }
        }
        if (_observations.Count > nodes.Count * nodes.Count * seconds)
        {
            throw new InvalidOperationException("Observed more publications than the bounded open-loop workload offered.");
        }
    }

    public OpenLoopSummary Complete(OpenLoopNodeReport[] reports)
    {
        if (reports.Length != nodes.Count || reports.Select(report => report.ProcessId).Distinct().Count() != nodes.Count
            || reports.Any(report => !nodes.TryGetValue(report.ProcessId, out var address) || address != report.Address
                || report.Report.Publications.Length != seconds || report.Report.MissedPeriods != 0 || report.Report.Overruns != 0))
        {
            throw new InvalidOperationException("Open-loop completion has missing producers, missed periods or overruns.");
        }

        var publications = reports.SelectMany(report => report.Report.Publications
            .Select(publication => (report.Address, Publication: publication)))
            .ToDictionary(item => (item.Address, item.Publication.Result.Version), item => item.Publication);
        var ages = new List<double>();
        foreach (var observation in _observations.Values)
        {
            if (!publications.TryGetValue((observation.Source, observation.Version), out var publication)
                || observation.Value != publication.Result.Value)
            {
                throw new InvalidOperationException($"Observed an unoffered or mismatched value from {observation.Source}.");
            }

            var age = (observation.ObservedAtUtc - publication.Result.StartedAtUtc).TotalMilliseconds;
            if (age < 0 || age > (seconds + 10) * 1000)
            {
                throw new InvalidOperationException("Open-loop observation timestamps exceed the bounded same-host latency window.");
            }
            if (nodes[observation.ObserverProcessId] != observation.Source)
            {
                ages.Add(age);
            }
        }

        var latestLatencies = new List<double>();
        foreach (var report in reports)
        {
            var latest = report.Report.Publications[^1].Result;
            var latencies = new List<double>();
            foreach (var processId in nodes.Keys)
            {
                if (!_observations.TryGetValue((processId, report.Address, latest.Version), out var observation))
                {
                    throw new InvalidOperationException($"Process {processId} has not observed the latest value from {report.Address}.");
                }
                latencies.Add((observation.ObservedAtUtc - latest.StartedAtUtc).TotalMilliseconds);
            }
            latestLatencies.Add(latencies.Max());
        }

        var all = reports.SelectMany(report => report.Report.Publications).ToArray();
        var lateness = all.Select(value => (value.Result.StartedAtUtc - value.ScheduledAtUtc).TotalMilliseconds).ToArray();
        var durations = all.Select(value => (value.Result.CompletedAtUtc - value.Result.StartedAtUtc).TotalMilliseconds).ToArray();
        if (lateness.Any(value => value < 0 || value >= 1000) || durations.Any(value => value < 0 || value >= 1000))
        {
            throw new InvalidOperationException("Actual open-loop publication timing exceeded its 1 Hz slot.");
        }

        var periods = reports.SelectMany(report => report.Report.Publications.Skip(1)
            .Zip(report.Report.Publications, (current, previous) => (current.Result.StartedAtUtc - previous.Result.StartedAtUtc).TotalMilliseconds))
            .ToArray();
        return new(seconds, 1, _observations.Count, nodes.Count * nodes.Count * seconds - _observations.Count,
            0, 0, lateness.Max(), periods, durations, ages.ToArray(), latestLatencies.ToArray());
    }
}
