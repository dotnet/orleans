using Xunit;

namespace Orleans.Dissemination.PerformanceHarness;

internal sealed partial class ProcessCluster
{
    public async Task<OpenLoopSummary> MeasureOpenLoop(string workload, int seconds)
    {
        var nodes = Active;
        var owned = nodes.ToDictionary(node => node.Last.Identity.ProcessId, node => node.Last.Address);
        var initialVersions = nodes.ToDictionary(node => node.Last.Address, node => node.Last.LoadVersions[node.Last.Address]);
        var measurement = new OpenLoopMeasurements(owned, initialVersions, seconds);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5 + seconds + 10));
        var cancellationToken = deadline.Token;
        var startUtc = DateTimeOffset.UtcNow.AddSeconds(5);
        var plans = nodes.Select((_, index) => new OpenLoopPlan(startUtc, seconds, index, nodes.Length, workload)).ToArray();
        await Save("open-loop-plans.json", plans);
        try
        {
            var armed = await Task.WhenAll(nodes.Select((node, index) =>
                node.Send(cancellationToken, "open-loop-arm", openLoop: plans[index])));
            Assert.True(DateTimeOffset.UtcNow < startUtc, "All open-loop workers must acknowledge arming before the common start epoch.");
            Assert.All(armed, snapshot =>
            {
                Assert.NotNull(snapshot.OpenLoopProgress);
                Assert.Equal(0, snapshot.OpenLoopProgress.PublishedCount);
                Assert.Null(snapshot.OpenLoopProgress.Error);
            });

            while (true)
            {
                var snapshots = await Sample();
                if (snapshots.All(snapshot => snapshot.OpenLoopProgress!.Completed))
                {
                    break;
                }
                await Task.Delay(100, cancellationToken);
            }

            var completed = await Task.WhenAll(nodes.Select(node => node.Send(cancellationToken, "open-loop-finish")));
            var reports = completed.Select(snapshot => new OpenLoopNodeReport(snapshot.Identity.ProcessId, snapshot.Address,
                snapshot.OpenLoopReport ?? throw new InvalidOperationException("Worker completion is missing its open-loop report."))).ToArray();
            await Save("open-loop-producers.json", reports);
            var expected = reports.ToDictionary(report => report.Address, report => report.Report.Publications[^1].Result.Value);
            while (true)
            {
                var snapshots = await Sample();
                if (snapshots.All(snapshot => MatchesExpectedLoad(snapshot.Load, expected)))
                {
                    break;
                }
                await Task.Delay(100, cancellationToken);
            }

            var result = measurement.Complete(reports);
            await Save("open-loop-summary.json", result);
            return result;
        }
        catch (Exception exception)
        {
            await Save("failure-state.json", new
            {
                Phase = "bounded open-loop production and exact latest-state convergence",
                Error = exception.ToString(),
                Plans = plans,
                Nodes = nodes.Select(node => node.Last),
            });
            throw;
        }
        finally
        {
            await Save("open-loop-observations.json", measurement.Observations);
        }

        async Task<NodeSnapshot[]> Sample()
        {
            var snapshots = await Task.WhenAll(nodes.Select(node => node.Send(cancellationToken, "snapshot")));
            Assert.Equal(owned.Count, snapshots.Select(snapshot => snapshot.Identity.ProcessId).Distinct().Count());
            foreach (var snapshot in snapshots)
            {
                Assert.True(owned.TryGetValue(snapshot.Identity.ProcessId, out var address));
                Assert.Equal(address, snapshot.Address);
                Assert.Equal(owned.Values.Order(StringComparer.Ordinal), snapshot.ActiveMembers);
                Assert.NotNull(snapshot.OpenLoopProgress);
                Assert.Null(snapshot.OpenLoopProgress.Error);
                Assert.Equal(0, snapshot.OpenLoopProgress.MissedPeriods);
                Assert.Equal(0, snapshot.OpenLoopProgress.Overruns);
                measurement.Observe(snapshot.Identity.ProcessId, snapshot.CapturedAtUtc, snapshot.LoadVersions, snapshot.Load);
            }
            return snapshots;
        }
    }
}
