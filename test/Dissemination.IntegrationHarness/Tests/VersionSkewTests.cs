using System.Text.Json;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Orleans.Dissemination.IntegrationHarness;

public sealed class VersionSkewTests
{
    [Theory]
    [InlineData("Old", false)]
    [InlineData("New", false)]
    [InlineData("New", true)]
    [Trait("Category", "DisseminationProcess")]
    public async Task PartitionHealing_ConfirmsTransportBeforeSinglePublicationRound(string runtime, bool enabled)
    {
        await using var cluster = new ProcessCluster($"partition-publication-{runtime}-{enabled}", fastRecovery: false);
        var sender = await cluster.Start(runtime, enabled);
        var receiver = await cluster.Start(runtime, enabled);
        await cluster.Stabilize();
        await cluster.AssertControlRpcs();
        await cluster.PublishAndConverge();

        for (var iteration = 0; iteration < 3; iteration++)
        {
            await receiver.Send("partition", value: true);
            if (iteration == 0)
            {
                var probe = await sender.Send("probe-echo", peer: receiver.Last.Address);
                Assert.Null(probe.RemoteProcessId);
                Assert.NotNull(probe.ProbeError);
            }

            await sender.Send("publish");
            await cluster.HealPartition(receiver);
            await cluster.PublishAndConverge();
            Assert.All(cluster.Active, node => Assert.False(node.Last.Partitioned));
            await cluster.Save($"healed-publication-{iteration}.json", cluster.Active.Select(node => node.Last));
        }
    }

    [Fact]
    [Trait("Category", "DisseminationProcess")]
    public async Task PinnedBinaries_RollForward_MixedEnablement_AndRollback()
    {
        await using var cluster = new ProcessCluster("rolling-upgrade");
        var old = new[]
        {
            await cluster.Start("Old", enabled: false),
            await cluster.Start("Old", enabled: false),
            await cluster.Start("Old", enabled: false),
        };
        await cluster.Stabilize();
        await cluster.AssertControlRpcs();
        await cluster.PublishAndConverge();

        var replacements = new List<SiloProcess>();
        for (var index = 0; index < old.Length; index++)
        {
            var replacement = await cluster.Start("New", enabled: index != 0);
            replacements.Add(replacement);
            Assert.NotEqual(old[index].Last.Identity.Sha256, replacement.Last.Identity.Sha256);
            Assert.NotEqual(old[index].Last.Identity.ModuleVersionId, replacement.Last.Identity.ModuleVersionId);
            await cluster.Stabilize();
            await cluster.AssertControlRpcs();
            await cluster.PublishAndConverge();
            if (index != 0)
            {
                // A working stable RPC on this exact connection brackets the unsupported new RPC.
                var probe = await replacement.Send("probe", peer: old[index].Last.Address);
                Assert.NotNull(probe.ProbeError);
                Assert.False(probe.ProbeTimedOut, $"Unknown-RPC compatibility must not be inferred from a timeout: {probe.ProbeError}");
                Assert.Equal(old[index].Last.Identity.ProcessId,
                    (await replacement.Send("echo", peer: old[index].Last.Address)).RemoteProcessId);
                Assert.Contains(old[index].Last.Address, replacement.Last.UnconfirmedPeers);
                var beforeFallback = old[index].Last.LoadVersions[replacement.Last.Address];
                await cluster.PublishAndConverge();
                Assert.True(old[index].Last.LoadVersions[replacement.Last.Address] > beforeFallback,
                    "The old process must receive a fresh exact load sample after the unsupported dissemination RPC.");
                Assert.Contains(old[index].Last.Address, replacement.Last.UnconfirmedPeers);
                Assert.Empty(old[index].Last.Applies);
                await cluster.Save($"old-rpc-probe-{index}.json", probe);
                await cluster.Save($"old-rpc-legacy-fallback-{index}.json", new
                {
                    BeforeVersion = beforeFallback,
                    Sender = replacement.Last,
                    LegacyReceiver = old[index].Last,
                });
            }

            await cluster.Save($"upgrade-{index}-before-stop.json", cluster.Active.Select(node => node.Last));
            await old[index].Stop();
            await cluster.Stabilize();
            await cluster.PublishAndConverge();
        }

        var disabled = replacements[0];
        var enabled = replacements.Skip(1).ToArray();
        await cluster.Eventually("enabled peers confirm support, default-disabled peers remain legacy", async () =>
        {
            foreach (var node in enabled)
            {
                await node.Send("snapshot");
            }

            return enabled.All(node =>
                node.Last.UnconfirmedPeers.Contains(disabled.Last.Address)
                && enabled.Where(peer => !ReferenceEquals(peer, node)).All(peer =>
                    !node.Last.UnconfirmedPeers.Contains(peer.Last.Address)));
        });
        var disabledProbe = await enabled[0].Send("probe", peer: disabled.Last.Address);
        Assert.Equal("namespace-unsupported", disabledProbe.ProbeError);
        await disabled.Send("snapshot");
        Assert.False(disabled.Last.Enabled);
        Assert.False(disabled.Last.NamespaceEnabled);
        Assert.Equal(0, Sum(disabled.Last, "orleans-dissemination-broadcast-sent|"));
        Assert.Equal(0, disabled.Last.Metrics.Where(pair =>
            pair.Key.StartsWith("orleans-dissemination-anti-entropy-exchanges|", StringComparison.Ordinal)
            && pair.Key.Contains("direction=out", StringComparison.Ordinal)).Sum(pair => pair.Value.Sum));
        Assert.Empty(disabled.Last.Applies);

        // The same original binary is reintroduced, not a flag on the new binary.
        for (var index = replacements.Count - 1; index >= 0; index--)
        {
            var rolledBack = await cluster.Start("Old", enabled: false, port: old[index].Configuration.SiloPort);
            Assert.Equal(old[index].Last.Identity.Sha256, rolledBack.Last.Identity.Sha256);
            Assert.NotEqual(replacements[index].Last.Identity.ProcessId, rolledBack.Last.Identity.ProcessId);
            await cluster.Stabilize();
            await cluster.AssertControlRpcs();
            await cluster.PublishAndConverge();
            await replacements[index].Stop();
            await cluster.Stabilize();
            await cluster.PublishAndConverge();
            await cluster.Save($"rollback-{index}.json", cluster.Active.Select(node => node.Last));
        }

        Assert.All(cluster.Active, node => Assert.Equal("Old", node.Last.Identity.Runtime));
    }

    [Fact]
    [Trait("Category", "DisseminationProcess")]
    public async Task PartitionHistoryAndHeartbeat_RequireDisseminationProvenance()
    {
        await using var cluster = new ProcessCluster("partition-history-heartbeat");
        var origin = await cluster.Start("New", enabled: true);
        var relay = await cluster.Start("New", enabled: true);
        var receiver = await cluster.Start("New", enabled: true);
        await cluster.Stabilize();
        await cluster.AssertControlRpcs();
        await cluster.PublishAndConverge();
        await cluster.Eventually("identical membership before isolation", async () =>
        {
            var snapshots = await Task.WhenAll(cluster.Active.Select(node => node.Send("refresh")));
            return snapshots.All(snapshot => SameMembership(snapshots[0], snapshot));
        });
        foreach (var node in cluster.Active)
        {
            var isolated = await node.Send("isolate-membership", value: true);
            Assert.True(isolated.LegacyGossipSuppressed);
            Assert.True(isolated.MembershipReadsFrozen);
        }

        Assert.False((await origin.Send("probe-tree", peer: receiver.Last.Address)).TreeProbeRejected);
        foreach (var node in cluster.Active)
        {
            var quiesced = await node.Send("block-tree", value: true);
            Assert.NotNull(quiesced.TreeGate);
            Assert.True(quiesced.TreeGate.Blocked);
            Assert.Equal(0, quiesced.TreeGate.InFlight);
        }

        // Verify the native PushBroadcast RPC is intercepted, not just that a local flag was set.
        Assert.True((await origin.Send("probe-tree", peer: receiver.Last.Address)).TreeProbeRejected);
        await Task.WhenAll(cluster.Active.Select(node => node.Send("snapshot")));
        var before = cluster.Active.ToDictionary(node => node.Last.Identity.ProcessId, node => node.Last);
        var oldVersion = receiver.Last.MembershipVersion;
        await receiver.Send("partition", value: true);
        var source = await origin.Send("membership-history", count: 40, version: oldVersion);
        Assert.Equal(oldVersion + 40, source.MembershipVersion);
        Assert.Equal(0, source.RepairFromVersion); // The actual production namespace evicted the old baseline.
        var blocked = await receiver.Send("snapshot");
        Assert.Equal(oldVersion, blocked.MembershipVersion);
        Assert.False(SameMembership(source, blocked));
        await receiver.Send("partition", value: false);

        await cluster.Eventually("anti-entropy-only full repair after a real transport partition and retained-history miss", async () =>
        {
            await receiver.Send("snapshot");
            await relay.Send("snapshot");
            return SameMembership(source, receiver.Last) && SameMembership(source, relay.Last)
                && receiver.Last.Applies.Any(evidence => evidence.Namespace == "membership"
                    && evidence.FromVersion == 0 && evidence.ToVersion == source.MembershipVersion
                    && evidence.Result == "Applied" && evidence.Peer is not null);
        });
        await AssertNoTreeAdmissions(cluster, before);
        Assert.True(receiver.Last.MembershipReadsFrozen);
        Assert.True(receiver.Last.LegacyGossipSuppressed);
        await cluster.Save("history-recovery.json", new { Before = before, After = cluster.Active.Select(node => node.Last) });

        var priorHeartbeatApplications = receiver.Last.Applies.Count(evidence =>
            evidence.Namespace == "membership" && evidence.Result == "Applied" && evidence.ToVersion == source.MembershipVersion);
        var heartbeat = await origin.Send("membership-heartbeat");
        Assert.Equal(source.MembershipVersion, heartbeat.MembershipVersion);
        Assert.NotNull(heartbeat.HeartbeatTicks);
        Assert.False(SameMembership(source, heartbeat));
        await cluster.Eventually("same-version heartbeat fingerprint repair without Publish, gossip, or table reads", async () =>
        {
            await receiver.Send("snapshot");
            await relay.Send("snapshot");
            return SameMembership(heartbeat, receiver.Last) && SameMembership(heartbeat, relay.Last)
                && receiver.Last.Applies.Count(evidence => evidence.Namespace == "membership"
                    && evidence.FromVersion == 0 && evidence.ToVersion == heartbeat.MembershipVersion
                    && evidence.Result == "Applied") > priorHeartbeatApplications;
        });
        await AssertNoTreeAdmissions(cluster, before);
        await cluster.Save("same-version-heartbeat-recovery.json", cluster.Active.Select(node => node.Last));

        var expectedLoad = receiver.Last.Load[origin.Last.Address];
        var expectedVersion = receiver.Last.LoadVersions[origin.Last.Address];
        var priorLoadApplications = receiver.Last.Applies.Count(evidence =>
            evidence.Namespace == "load" && evidence.Result == "Applied" && evidence.ToVersion == expectedVersion);
        await receiver.Send("forget-load", peer: origin.Last.Address);
        Assert.DoesNotContain(origin.Last.Address, receiver.Last.Load.Keys);
        await cluster.Eventually("load repair reaches exact state with no new publication or direct refresh", async () =>
        {
            await receiver.Send("snapshot");
            return receiver.Last.Load.TryGetValue(origin.Last.Address, out var actual)
                && actual == expectedLoad
                && receiver.Last.Applies.Count(evidence => evidence.Namespace == "load"
                    && evidence.FromVersion == 0 && evidence.ToVersion == expectedVersion
                    && evidence.Result == "Applied") > priorLoadApplications;
        });
        await AssertNoTreeAdmissions(cluster, before);
        await cluster.Save("load-full-repair.json", receiver.Last);
        foreach (var node in cluster.Active)
        {
            await node.Send("isolate-membership", value: false);
            await node.Send("block-tree", value: false);
        }
    }

    [Fact]
    [Trait("Category", "DisseminationProcess")]
    public async Task RuntimeStatisticsComparison_ObservesPublicReadonlyFieldMutation()
    {
        foreach (var runtime in new[] { "Old", "New" })
        {
            await using var cluster = new ProcessCluster($"statistics-projection-{runtime}");
            var node = await cluster.Start(runtime, enabled: false);
            var result = await node.Send("verify-load-comparison");
            var probe = Assert.IsType<StateComparisonProbe>(result.ComparisonProbe);
            Assert.NotEqual(probe.Before, probe.After);
            using var original = JsonDocument.Parse(probe.Before);
            using var changed = JsonDocument.Parse(probe.After);
            var originalEnvironment = original.RootElement.GetProperty("EnvironmentStatistics");
            var changedEnvironment = changed.RootElement.GetProperty("EnvironmentStatistics");
            Assert.Contains("RawCpuUsagePercentage", probe.PublicFields);
            Assert.All(probe.PublicFields, field =>
            {
                Assert.True(originalEnvironment.TryGetProperty(field, out _), $"Missing original field {field}");
                Assert.True(changedEnvironment.TryGetProperty(field, out _), $"Missing changed field {field}");
            });
            Assert.Equal(20, originalEnvironment.GetProperty("RawCpuUsagePercentage").GetSingle());
            Assert.Equal(35, changedEnvironment.GetProperty("RawCpuUsagePercentage").GetSingle());
            await cluster.Save("statistics-field-mutation.json", probe);
        }
    }

    [Fact]
    [Trait("Category", "DisseminationProcess")]
    public async Task RequiredCancellationAndPartitionedShutdown_AreBounded()
    {
        await using var cluster = new ProcessCluster("bounded-shutdown");
        var sender = await cluster.Start("New", enabled: true);
        var receiver = await cluster.Start("New", enabled: true);
        await cluster.Stabilize();
        await cluster.AssertControlRpcs();
        var previousStarted = receiver.Last.StartedControlCalls;
        var previousCancelled = receiver.Last.CancelledControlCalls;
        var previousSignals = receiver.Last.ControlCancellationSignals;
        await sender.Send("start-cancel-rpc", peer: receiver.Last.Address);
        await cluster.Eventually("the remote Hold has entered with a live token before cancellation is issued", async () =>
        {
            await receiver.Send("snapshot");
            return receiver.Last.PendingControlCalls == 1
                && receiver.Last.StartedControlCalls == previousStarted + 1
                && receiver.Last.CancelledControlCalls == previousCancelled
                && receiver.Last.ControlCancellationSignals == previousSignals
                && receiver.Last.ControlTokenCanBeCanceled
                && !receiver.Last.ControlTokenCancelledOnEntry
                && receiver.Last.ControlStartedAtUtc.HasValue;
        });
        await sender.Send("snapshot");
        Assert.NotNull(sender.Last.OutboundControlCall);
        Assert.False(sender.Last.OutboundControlCall.CancellationRequested);
        Assert.False(sender.Last.OutboundControlCall.RawTaskCompleted);
        await cluster.Save("cancellation-started.json", new { Sender = sender.Last, Receiver = receiver.Last });
        await sender.Send("cancel-started-rpc");
        Assert.True(sender.Last.OutboundControlCall!.CancellationRequested);
        Assert.True(sender.Last.OutboundControlCall.RawTaskCompleted);
        await cluster.Eventually("the remote cancellable test RPC releases its server-side request", async () =>
        {
            await receiver.Send("snapshot");
            return receiver.Last.PendingControlCalls == 0
                && receiver.Last.StartedControlCalls == previousStarted + 1
                && receiver.Last.CancelledControlCalls == previousCancelled + 1
                && receiver.Last.ControlCancellationSignals == previousSignals + 1
                && receiver.Last.ControlCancellationObservedAtUtc.HasValue;
        });
        await cluster.Save("cancellation-observed.json", new { Sender = sender.Last, Receiver = receiver.Last });
        await receiver.Send("partition", value: true);
        await sender.Send("publish", count: 4);
        await sender.Stop();
        Assert.False(sender.ForcedKill);
        Assert.InRange(sender.ShutdownMilliseconds, 0, 35_000);
        await receiver.Send("partition", value: false);
        await receiver.Stop();
        Assert.False(receiver.ForcedKill);
        Assert.InRange(receiver.ShutdownMilliseconds, 0, 35_000);
    }

    internal static bool SameMembership(NodeSnapshot left, NodeSnapshot right) =>
        left.MembershipVersion == right.MembershipVersion && left.Membership.SequenceEqual(right.Membership);

    private static async Task AssertNoTreeAdmissions(ProcessCluster cluster, Dictionary<int, NodeSnapshot> before)
    {
        foreach (var node in cluster.Active)
        {
            var snapshot = await node.Send("snapshot");
            Assert.NotNull(snapshot.TreeGate);
            Assert.True(snapshot.TreeGate.Blocked);
            Assert.Equal(0, snapshot.TreeGate.InFlight);
            Assert.Equal(before[snapshot.Identity.ProcessId].TreeGate!.Admitted, snapshot.TreeGate.Admitted);
            Assert.True(snapshot.LegacyGossipSuppressed);
            Assert.True(snapshot.MembershipReadsFrozen);
        }
    }

    internal static double Sum(NodeSnapshot snapshot, string prefix) =>
        snapshot.Metrics.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)).Sum(pair => pair.Value.Sum);
}
