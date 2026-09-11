using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;

namespace Orleans.Dissemination.IntegrationHarness;

public sealed class ScalingTests
{
    [Fact]
    [Trait("Category", "DisseminationScale")]
    public async Task Scaling_OriginalLegacy_DefaultOff_EnabledSupported()
    {
        var sizes = (Environment.GetEnvironmentVariable("ORLEANS_DISSEMINATION_SIZES") ?? "4,8,16")
            .Split(',').Select(value => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var iterations = Setting("ITERATIONS", 10, 3, 200);
        var repetitions = Setting("REPETITIONS", 1, 1, 5);
        Assert.All(sizes, size => Assert.InRange(size, 3, 32));
        Assert.InRange(sizes.Length, 1, 6);

        var records = new List<object>();
        var output = Environment.GetEnvironmentVariable("ORLEANS_DISSEMINATION_RESULTS")!;
        RuntimeVariant[] variants =
        [
            new("OriginalLegacy", "Old", Enabled: false),
            new("CurrentDefaultOff", "New", Enabled: false),
            new("CurrentEnabledSupported", "New", Enabled: true),
        ];
        foreach (var size in sizes)
        {
            foreach (var scenario in new[] { "stable", "churn", "partition" })
            {
                for (var repetition = 0; repetition < repetitions; repetition++)
                {
                    // Rotate all three paths to expose thermal/cache/order effects across repetitions.
                    for (var order = 0; order < variants.Length; order++)
                    {
                        var variant = variants[(repetition + order) % variants.Length];
                        var record = await Measure(size, scenario, variant, iterations, repetition, order);
                        records.Add(record);
                        await File.WriteAllTextAsync(Path.Combine(output, "cost-results.json"),
                            JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }),
                            TestContext.Current.CancellationToken);
                    }
                }
            }
        }
    }

    private static async Task<object> Measure(int size, string scenario, RuntimeVariant variant, int iterations, int repetition, int order)
    {
        var enabled = variant.Enabled;
        await using var cluster = new ProcessCluster($"scale-{size}-{scenario}-{variant.Name}-{repetition}", fastRecovery: false);
        for (var index = 0; index < size; index++)
        {
            await cluster.Start(variant.Runtime, enabled);
        }

        await cluster.Stabilize();
        await cluster.AssertControlRpcs(allPairs: false);
        await cluster.PublishAndConverge();
        await cluster.PublishAndConverge();
        if (enabled)
        {
            await ConfirmSupport(cluster);
        }

        await cluster.Save("warmup.json", await Task.WhenAll(cluster.Active.Select(node => node.Send("measure"))));
        // Socket EventCounters arrive in one-second buckets. Every path has the same two-second margins.
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        var start = (await Task.WhenAll(cluster.Active.Select(node => node.Send("measure"))))
            .ToDictionary(snapshot => snapshot.Identity.ProcessId);
        var startedAt = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        var latencies = new List<double>();
        var recoveryLatencies = new List<double>();
        var offeredPublications = 0;
        var restarts = 0;
        var partitions = 0;
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            if (scenario == "churn" && iteration > 0 && iteration % 2 == 0)
            {
                var victim = cluster.Active[iteration % size];
                await victim.Stop();
                // Reuse the endpoint to exercise a real new silo incarnation and stale-generation cleanup.
                await cluster.Start(variant.Runtime, enabled, victim.Configuration.SiloPort);
                await cluster.Stabilize();
                if (enabled)
                {
                    // Bootstrap/fallback work stays inside the churn window, but offered rounds use supported peers.
                    await ConfirmSupport(cluster);
                }

                restarts++;
            }
            else if (scenario == "partition" && iteration % 3 == 1)
            {
                var victim = cluster.Active[iteration % size];
                await victim.Send("partition", value: true);
                partitions++;
                foreach (var source in cluster.Active.Where(node => !ReferenceEquals(node, victim)))
                {
                    await source.Send("publish");
                    offeredPublications++;
                }

                var recovery = Stopwatch.StartNew();
                // Establish delivery paths before offering the next sample, including on the one-way legacy path.
                await cluster.HealPartition(victim);
                latencies.Add(await cluster.PublishAndConverge());
                offeredPublications += size;
                recoveryLatencies.Add(recovery.Elapsed.TotalMilliseconds);
                continue;
            }

            latencies.Add(await cluster.PublishAndConverge());
            offeredPublications += size;
        }

        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await Task.WhenAll(cluster.Active.Select(node => node.Send("measure")));
        var elapsed = clock.Elapsed.TotalMilliseconds;
        var finishedAt = DateTimeOffset.UtcNow;
        var finish = cluster.All.Select(node => node.Last).ToArray();
        var retained = await Task.WhenAll(cluster.Active.Select(node => node.Send("retained-memory")));
        var retainedByPid = retained.ToDictionary(snapshot => snapshot.Identity.ProcessId, snapshot => snapshot.ManagedHeapBytes);
        Assert.Equal(scenario == "churn" ? (iterations - 1) / 2 : 0, restarts);
        Assert.Equal(scenario == "partition" ? (iterations + 1) / 3 : 0, partitions);
        if (enabled)
        {
            Assert.All(start.Values, snapshot => Assert.Empty(snapshot.UnconfirmedPeers));
            Assert.All(cluster.Active, node => Assert.Empty(node.Last.UnconfirmedPeers));
        }

        var nodes = finish.Select(snapshot =>
        {
            start.TryGetValue(snapshot.Identity.ProcessId, out var baseline);
            var allMessages = Delta(snapshot, baseline, "orleans-messaging-sent-messages-size|");
            var requestMessages = Delta(snapshot, baseline, "orleans-messaging-sent-messages-size|", "MessageDirection=Request");
            var oneWayMessages = Delta(snapshot, baseline, "orleans-messaging-sent-messages-size|", "MessageDirection=OneWay");
            var received = Delta(snapshot, baseline, "orleans-messaging-received-messages-size|");
            var topology = snapshot.TopologyMembers.Contains(snapshot.Address)
                ? snapshot
                : baseline ?? cluster.All.Single(node => node.Last.Identity.ProcessId == snapshot.Identity.ProcessId).Initial;
            var index = Array.IndexOf(topology.TopologyMembers, snapshot.Address);
            return new NodeCost(
                snapshot.Identity.ProcessId,
                snapshot.Address,
                index >= 0 ? index : null,
                index >= 0 && index < topology.Fanout,
                topology.OriginatorTargets,
                topology.ForwardingTargets,
                requestMessages.Count + oneWayMessages.Count,
                allMessages.Count,
                allMessages.Sum,
                received.Count,
                received.Sum,
                snapshot.TransportBytesWritten - (baseline?.TransportBytesWritten ?? 0),
                snapshot.TransportBytesRead - (baseline?.TransportBytesRead ?? 0),
                snapshot.SocketBytesSent - (baseline?.SocketBytesSent ?? 0),
                snapshot.SocketBytesReceived - (baseline?.SocketBytesReceived ?? 0),
                snapshot.SocketCounterSamples - (baseline?.SocketCounterSamples ?? 0),
                snapshot.CpuMilliseconds - (baseline?.CpuMilliseconds ?? 0),
                snapshot.AllocatedBytes - (baseline?.AllocatedBytes ?? 0),
                retainedByPid.GetValueOrDefault(snapshot.Identity.ProcessId),
                snapshot.WorkingSetBytes,
                snapshot.PrivateBytes,
                Delta(snapshot, baseline, "orleans-dissemination-bytes-sent|").Sum,
                Delta(snapshot, baseline, "orleans-dissemination-broadcast-sent|").Sum,
                Delta(snapshot, baseline, "orleans-dissemination-anti-entropy-exchanges|", "direction=out").Sum);
        }).ToArray();
        Assert.All(nodes, node =>
        {
            Assert.True(node.SentMessages > 0, "Orleans message histogram evidence is missing.");
            Assert.True(node.SerializedBytesSent > 0, "Serialized byte evidence is missing.");
            Assert.True(node.SocketSamples > 0 && node.SocketBytesSent > 0, "OS socket byte evidence is missing.");
        });
        if (!enabled)
        {
            Assert.All(nodes, node =>
            {
                Assert.Equal(0, node.DisseminationBroadcasts);
                Assert.Equal(0, node.AntiEntropyExchanges);
            });
        }
        else
        {
            Assert.True(nodes.Sum(node => node.DisseminationBroadcasts) > 0,
                "The supported-enabled cost window did not exercise any dissemination broadcasts.");
        }

        var record = new
        {
            Schema = 1,
            Profile = Environment.GetEnvironmentVariable("ORLEANS_DISSEMINATION_PROFILE") ?? "Standalone",
            RuntimePath = variant.Name,
            Runtime = finish[0].Identity,
            Size = size,
            Scenario = scenario,
            Enabled = enabled,
            Iterations = iterations,
            Repetition = repetition,
            ExecutionOrder = order,
            OfferedPublications = offeredPublications,
            RestartedProcesses = restarts,
            InjectedPartitions = partitions,
            WindowMilliseconds = elapsed,
            WindowStartedAtUtc = startedAt,
            WindowFinishedAtUtc = finishedAt,
            ConvergenceMilliseconds = latencies,
            PartitionRecoveryMilliseconds = recoveryLatencies,
            TotalRpcs = nodes.Sum(node => node.Rpcs),
            TotalMessages = nodes.Sum(node => node.SentMessages),
            SerializedBytesSent = nodes.Sum(node => node.SerializedBytesSent),
            SocketBytesSent = nodes.Sum(node => node.SocketBytesSent),
            TransportBytesSubmitted = nodes.Sum(node => node.TransportBytesSubmitted),
            CpuMilliseconds = nodes.Sum(node => node.CpuMilliseconds),
            AllocatedBytes = nodes.Sum(node => node.AllocatedBytes),
            RetainedManagedBytes = nodes.Sum(node => node.RetainedManagedBytes),
            Nodes = nodes,
            Environment = new { RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription, System.Environment.ProcessorCount },
            Methodology = new
            {
                WarmupRounds = 2,
                SettlingMarginSeconds = 2,
                AutomaticLoadPublisherPaused = true,
                ProductionDisseminationDefaults = variant.Runtime == "New",
                LoadNamespaceSupportConfirmedBeforeOfferedRounds = enabled,
                IncludesUnconfirmedBootstrapTraffic = enabled && scenario == "churn",
                LivenessFailureDetectionEnabled = false,
                StorageProvider = "locked local JSON file; no external-service network",
                Window = "after warmup/capability discovery, through offered load, churn/restarts and final 2-second drain; GC retained-size probe is outside CPU/allocation window",
                RpcDefinition = "sent Orleans message-size observations tagged MessageDirection=Request OR MessageDirection=OneWay; not response messages",
                SerializedBytes = "complete serialized Orleans messages (headers and bodies), not dissemination payload counters",
                SocketBytes = "System.Net.Sockets bytes-sent/bytes-received EventCounters, 1-second sampling; cumulative values differenced across the window (incremental counters also supported); excludes TCP/IP headers and retransmits",
                TransportBytes = "bytes advanced at the real connection transport pipe, including handshake; may include buffered bytes lost when a partition aborts a connection",
                RetainedMemory = "full GC live managed bytes on surviving nodes only; not a peak; RSS/private bytes and retired-node snapshots also retained",
                Includes = "all silo work during the window, including control/snapshot/instrumentation overhead and legacy fallback; restart startup/shutdown costs included for churn",
                Observation = "state polling on all three paths; detailed counters only at window boundaries; per-value DiagnosticListener capture disabled in scale runs",
                Topology = "per-node actual tree at end, or start for a retired node; raw command snapshots preserve intervening topology changes; role is not time-weighted",
                Excludes = "controller process CPU/allocation, storage disk byte accounting, cross-machine latency, NIC/TCP wire overhead, production storage/placement/grain work",
                PartitionComparison = "retire partitioned connections at both endpoints, then confirm healed connections with bidirectional acknowledged control RPCs before the same single publication round on all paths; teardown and readiness remain inside partition recovery time and total cost; silent anti-entropy recovery is asserted separately in VersionSkewTests",
                OriginalBaselineAttribution = "original-to-current default-off deltas include all intervening source changes, including lifecycle prerequisites and load notification/tombstone/dedup changes; only current off/on isolates enabling the subsystem on the same binary",
                Interpretation = "separate original-legacy, current-default-off and current-enabled-supported measurements; no asserted improvement or threshold",
            },
        };
        await cluster.Save("window-start.json", start);
        await cluster.Save("window-finish.json", finish);
        await cluster.Save("cost.json", record);
        return record;
    }

    private static Task ConfirmSupport(ProcessCluster cluster) =>
        cluster.Eventually("all enabled peers confirm support before offered publication rounds", async () =>
        {
            var snapshots = await Task.WhenAll(cluster.Active.Select(node => node.Send("snapshot")));
            return snapshots.All(snapshot => snapshot.UnconfirmedPeers.Length == 0);
        }, TimeSpan.FromSeconds(90));

    private sealed record RuntimeVariant(string Name, string Runtime, bool Enabled);

    private static MetricValue Delta(NodeSnapshot value, NodeSnapshot? baseline, string prefix, string? tag = null)
    {
        var entries = value.Metrics.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)
            && (tag is null || pair.Key.Contains(tag, StringComparison.Ordinal)));
        long count = 0;
        double sum = 0;
        foreach (var (key, metric) in entries)
        {
            var previous = baseline?.Metrics.GetValueOrDefault(key);
            count += metric.Count - (previous?.Count ?? 0);
            sum += metric.Sum - (previous?.Sum ?? 0);
        }

        return new(count, sum);
    }

    private static int Setting(string name, int defaultValue, int minimum, int maximum)
    {
        var value = Environment.GetEnvironmentVariable("ORLEANS_DISSEMINATION_" + name);
        var result = value is null ? defaultValue : int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(result, minimum, maximum);
        return result;
    }

    private sealed record NodeCost(
        int ProcessId,
        string Address,
        int? TreeRank,
        bool UpperTree,
        string[] OriginatorTargets,
        string[] ForwardingTargets,
        long Rpcs,
        long SentMessages,
        double SerializedBytesSent,
        long ReceivedMessages,
        double SerializedBytesReceived,
        long TransportBytesSubmitted,
        long TransportBytesRead,
        double SocketBytesSent,
        double SocketBytesReceived,
        long SocketSamples,
        double CpuMilliseconds,
        long AllocatedBytes,
        long RetainedManagedBytes,
        long WorkingSetBytes,
        long PrivateBytes,
        double DisseminationPayloadBytes,
        double DisseminationBroadcasts,
        double AntiEntropyExchanges);
}
