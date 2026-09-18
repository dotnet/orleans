using Orleans.Runtime;
using Xunit;
using static Orleans.Clustering.TestKit.MembershipTableTestData;

namespace Orleans.Clustering.TestKit.Tests;

public sealed class MembershipTableSnapshotTests
{
    private static ClusteringMembershipSnapshot Snapshot(MembershipEntry entry, int version = 4, string tableToken = "table-4", string rowToken = "row-1")
        => ClusteringMembershipSnapshot.Capture(new(Tuple.Create(entry, rowToken), new(version, tableToken)));

    [Fact]
    public void Capture_DetachesEntriesAndNestedSuspectLists()
    {
        var entry = CreateEntry(1);
        var suspects = entry.SuspectTimes!;
        var captured = Snapshot(entry);
        MutateCaller(entry);
        suspects.Clear();
        var row = Assert.Single(captured.Rows).Value.Entry;
        Assert.Equal("host-1", row.HostName);
        Assert.Equal(SiloStatus.Created, row.Status);
        Assert.Equal(22001, row.ProxyPort);
        Assert.Equal(T0, row.IAmAliveTime);
        Assert.Equal(2, row.Suspects.Length);
        Assert.Equal("127.0.0.1:11001@10", row.Suspects[0].Identity);
        Assert.Equal(T0.AddSeconds(-10), row.Suspects[0].Time);
    }

    [Theory]
    [InlineData("IAmAliveTime")]
    [InlineData("row ETag")]
    [InlineData("table ETag")]
    [InlineData("table integer")]
    public void CompareComplete_DetectsHeartbeatAndBothTokenChanges(string field)
    {
        var entry = CreateEntry(1);
        var expected = Snapshot(entry);
        if (field == "IAmAliveTime") entry.IAmAliveTime = T2;
        var actual = Snapshot(entry, field == "table integer" ? 5 : 4,
            field == "table ETag" ? "different-table" : "table-4", field == "row ETag" ? "different-row" : "row-1");
        Assert.Contains(field, expected.CompareComplete(actual)!);
        Assert.Null(expected.CompareComplete(Snapshot(CreateEntry(1))));
    }

    [Theory]
    [InlineData("Identity")]
    [InlineData("Status")]
    [InlineData("ProxyPort")]
    [InlineData("HostName")]
    [InlineData("SiloName")]
    [InlineData("RoleName")]
    [InlineData("UpdateZone")]
    [InlineData("FaultZone")]
    [InlineData("StartTime")]
    [InlineData("SuspectTimes")]
    public void CompareCanonical_IncludesEveryFieldExceptHeartbeat(string field)
    {
        var expected = Snapshot(CreateEntry(1));
        var changed = CreateEntry(1);
        switch (field)
        {
            case "Identity": changed.SiloAddress = CreateSuccessor(changed).SiloAddress; break;
            case "Status": changed.Status = SiloStatus.Joining; break;
            case "ProxyPort": changed.ProxyPort++; break;
            case "HostName": changed.HostName = "changed-host"; break;
            case "SiloName": changed.SiloName = "changed-name"; break;
            case "RoleName": changed.RoleName = "changed-role"; break;
            case "UpdateZone": changed.UpdateZone++; break;
            case "FaultZone": changed.FaultZone++; break;
            case "StartTime": changed.StartTime = T1; break;
            case "SuspectTimes": changed.SuspectTimes![0] = Tuple.Create(CreateEntry(8).SiloAddress, T2); break;
        }
        Assert.NotNull(expected.CompareCanonical(Snapshot(changed)));
        var heartbeatOnly = CreateEntry(1);
        heartbeatOnly.IAmAliveTime = T2;
        Assert.Null(expected.CompareCanonical(Snapshot(heartbeatOnly)));
        Assert.Contains("row ETag", expected.CompareCanonical(Snapshot(heartbeatOnly, rowToken: "heartbeat-token"))!);
        Assert.Contains("IAmAliveTime", expected.CompareComplete(Snapshot(heartbeatOnly, rowToken: "heartbeat-token"))!);
    }

    [Fact]
    public void CompareCanonical_IgnoresEnumerationOrderAndNormalizesEmptySuspects()
    {
        var first = CreateEntry(1);
        var second = CreateEntry(2);
        second.SuspectTimes = null;
        var expected = ClusteringMembershipSnapshot.Capture(new(new List<Tuple<MembershipEntry, string>>
            { Tuple.Create(first, "a"), Tuple.Create(second, "b") }, new(3, "v")));
        first.SuspectTimes!.Reverse();
        second.SuspectTimes = [];
        var actual = ClusteringMembershipSnapshot.Capture(new(new List<Tuple<MembershipEntry, string>>
            { Tuple.Create(second, "b"), Tuple.Create(first, "a") }, new(3, "v")));
        Assert.Null(expected.CompareComplete(actual));
        Assert.Equal(2, actual.Rows.Count);
        second.SuspectTimes.Add(Tuple.Create(first.SiloAddress, T0));
        Assert.Contains("SuspectTimes", expected.Rows[second.SiloAddress.ToParsableString()].Entry.Difference(MembershipEntrySnapshot.Capture(second), false)!);
    }

    [Fact]
    public void CompareCanonical_DistinguishesGenerationsAtSameEndpoint()
    {
        var first = CreateEntry(1);
        var successor = CreateSuccessor(first);
        var expected = Snapshot(first);
        Assert.Contains("missing identity", expected.CompareCanonical(Snapshot(successor))!);
        Assert.Equal(first.SiloAddress.Endpoint, successor.SiloAddress.Endpoint);
        Assert.NotEqual(first.SiloAddress.ToParsableString(), successor.SiloAddress.ToParsableString());
    }

    [Fact]
    public void CompareCanonical_DetectsTimestampOnlyChangeForExistingSuspector()
    {
        var entry = CreateEntry(1);
        var expected = Snapshot(entry);
        var voter = entry.SuspectTimes![0].Item1;
        entry.SuspectTimes[0] = Tuple.Create(voter, T2);
        Assert.Contains("SuspectTimes", expected.CompareCanonical(Snapshot(entry))!);
        Assert.Equal(voter.ToParsableString(), expected.Rows[entry.SiloAddress.ToParsableString()].Entry.Suspects[0].Identity);
        Assert.Equal(T0.AddSeconds(-10), expected.Rows[entry.SiloAddress.ToParsableString()].Entry.Suspects[0].Time);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("heartbeat")]
    [InlineData("first-vote")]
    [InlineData("last-vote")]
    public void GetEffectiveUpdateTime_UsesStartHeartbeatAndEveryVote(string newest)
    {
        var entry = CreateEntry(1);
        if (newest == "start") entry.StartTime = T2;
        if (newest == "heartbeat") entry.IAmAliveTime = T2;
        if (newest == "first-vote") entry.SuspectTimes![0] = Tuple.Create(CreateEntry(2).SiloAddress, T2);
        if (newest == "last-vote") entry.SuspectTimes![1] = Tuple.Create(CreateEntry(2).SiloAddress, T2);
        var effective = MembershipEntrySnapshot.Capture(entry).GetEffectiveUpdateTime();
        Assert.Equal(T2, effective);
        Assert.Equal(DateTimeKind.Utc, effective.Kind);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void History_DeadCompactionPreservesTerminalIdentity(int cleanupVersion)
    {
        var dead = CreateEntry(1, status: SiloStatus.Dead);
        var history = new MembershipHistory();
        history.Observe(Snapshot(dead));
        history.Observe(ClusteringMembershipSnapshot.Capture(new(new TableVersion(cleanupVersion, $"table-{cleanupVersion}"))));
        Assert.True(history.IsTerminal(dead.SiloAddress.ToParsableString()));
        dead.Status = SiloStatus.Active;
        var exception = Assert.Throws<ClusteringConformanceException>(() => history.Observe(Snapshot(dead, 6, "table-6")));
        Assert.Contains("terminal identity", exception.Message);
    }

    [Theory]
    [InlineData(SiloStatus.Created)]
    [InlineData(SiloStatus.Joining)]
    [InlineData(SiloStatus.Active)]
    [InlineData(SiloStatus.ShuttingDown)]
    [InlineData(SiloStatus.Stopping)]
    public void History_SameVersionRejectsNonDeadRowRemoval(SiloStatus status)
    {
        var live = CreateEntry(1, status: status);
        var history = new MembershipHistory();
        history.Observe(Snapshot(live));

        var exception = Assert.Throws<ClusteringConformanceException>(() =>
            history.Observe(ClusteringMembershipSnapshot.Capture(new(new TableVersion(4, "table-4")))));

        Assert.Contains("same-version drift", exception.Message);
        Assert.Contains("missing identity=" + live.SiloAddress.ToParsableString(), exception.Message);
        Assert.False(history.IsTerminal(live.SiloAddress.ToParsableString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cleanup_BatchedSnapshots_RequireCoherentDeletions(bool versioned)
    {
        var first = CreateEntry(1, status: SiloStatus.Dead);
        var second = CreateEntry(2, status: SiloStatus.Dead);
        var live = CreateEntry(3, status: SiloStatus.Active);
        var before = ClusteringMembershipSnapshot.Capture(new(new List<Tuple<MembershipEntry, string>>
        {
            Tuple.Create(first, "r1"), Tuple.Create(second, "r2"), Tuple.Create(live, "r3")
        }, new(10, "v10")));
        var intermediate = before with { Version = versioned ? 11 : 10, TableEtag = versioned ? "v11" : "v10", Rows = before.Rows.Remove(first.SiloAddress.ToParsableString()) };
        var final = intermediate with { Version = versioned ? 12 : 10, TableEtag = versioned ? "v12" : "v10", Rows = intermediate.Rows.Remove(second.SiloAddress.ToParsableString()) };

        MembershipTableTestRunner.AssertCleanup(before, intermediate, T1, requireAllEligible: false);
        MembershipTableTestRunner.AssertCleanup(intermediate, final, T1);
        MembershipTableTestRunner.AssertCleanup(before, final, T1);
        var history = new MembershipHistory();
        history.Observe(before);
        history.Observe(intermediate);
        history.Observe(final);
        Assert.Equal(SiloStatus.Active, Assert.Single(final.Rows).Value.Entry.Status);
        Assert.Equal(versioned ? 12 : 10, final.Version);
        Assert.Equal(3, before.Rows.Count);

        var skippedEmptyBatch = intermediate with { Version = 12, TableEtag = "v12" };
        Assert.Contains("cleanup version", Assert.Throws<ClusteringConformanceException>(() =>
            MembershipTableTestRunner.AssertCleanup(before, skippedEmptyBatch, T1, requireAllEligible: false)).Message);
        var rollback = intermediate with { Version = before.Version - 1, TableEtag = before.TableEtag };
        Assert.Contains("cleanup version", Assert.Throws<ClusteringConformanceException>(() =>
            MembershipTableTestRunner.AssertCleanup(before, rollback, T1, requireAllEligible: false)).Message);
        var changed = intermediate with
        {
            Rows = intermediate.Rows.SetItem(live.SiloAddress.ToParsableString(),
            intermediate.Row(live.SiloAddress) with { Entry = intermediate.Row(live.SiloAddress).Entry with { HostName = "drift" } })
        };
        Assert.Contains("HostName", Assert.Throws<ClusteringConformanceException>(() =>
            MembershipTableTestRunner.AssertCleanup(before, changed, T1, requireAllEligible: false)).Message);
        var regressed = intermediate with
        {
            Rows = intermediate.Rows.SetItem(live.SiloAddress.ToParsableString(),
            intermediate.Row(live.SiloAddress) with { Entry = intermediate.Row(live.SiloAddress).Entry with { IAmAliveTime = T0.AddSeconds(-1) } })
        };
        MembershipTableTestRunner.AssertCleanup(before, regressed, T1, requireAllEligible: false);
        Assert.Throws<ClusteringConformanceException>(() =>
            MembershipTableTestRunner.AssertCleanup(before, before with { Version = 11, TableEtag = "v11" }, T1, requireAllEligible: false));
    }

    [Fact]
    public void History_NewerViewInfersDeathForMissingLiveIdentityAndRejectsResurrection()
    {
        var live = CreateEntry(1, status: SiloStatus.Active);
        var history = new MembershipHistory();
        history.Observe(Snapshot(live));
        history.Observe(ClusteringMembershipSnapshot.Capture(new(new TableVersion(7, "table-7"))));
        Assert.True(history.IsTerminal(live.SiloAddress.ToParsableString()));

        var failure = Assert.Throws<ClusteringConformanceException>(() => history.Observe(Snapshot(live, 8, "table-8")));
        Assert.Contains("terminal identity reappeared live", failure.Message);
        var successor = CreateSuccessor(live);
        history.Observe(Snapshot(successor, 8, "table-8"));
        Assert.True(history.IsTerminal(live.SiloAddress.ToParsableString()));
        Assert.False(history.IsTerminal(successor.SiloAddress.ToParsableString()));
    }

    [Fact]
    public void History_SameVersionDeadPruningRetainsCanonicalFields()
    {
        var live = CreateEntry(1, status: SiloStatus.Active);
        var dead = CreateEntry(2, status: SiloStatus.Dead);
        var initial = ClusteringMembershipSnapshot.Capture(new(new List<Tuple<MembershipEntry, string>>
            { Tuple.Create(live, "live"), Tuple.Create(dead, "dead") }, new(4, "table-4")));
        var history = new MembershipHistory();
        history.Observe(initial);
        live.IAmAliveTime = T2;
        history.Observe(Snapshot(live, rowToken: "live"));
        Assert.True(history.IsTerminal(dead.SiloAddress.ToParsableString()));
    }

    [Fact]
    public void CanonicalObservations_AcceptRawHeartbeatLagAndRejectLogicalTokenChanges()
    {
        var entry = CreateEntry(1, status: SiloStatus.Active);
        entry.IAmAliveTime = T2;
        var before = Snapshot(entry);
        var history = new MembershipHistory();
        history.Observe(before);
        entry.IAmAliveTime = T0;
        var lagged = Snapshot(entry);

        MembershipTableTestRunner.AssertHeartbeat(before, lagged);
        history.Observe(lagged);
        Assert.Null(before.CompareCanonical(lagged));
        Assert.Contains("IAmAliveTime", before.CompareComplete(lagged)!);
        Assert.Contains("row ETag", Assert.Throws<ClusteringConformanceException>(() =>
            MembershipTableTestRunner.AssertHeartbeat(before, Snapshot(entry, rowToken: "changed"))).Message);
        Assert.Contains("table ETag", Assert.Throws<ClusteringConformanceException>(() =>
            MembershipTableTestRunner.AssertHeartbeat(before, Snapshot(entry, tableToken: "changed"))).Message);
        entry.ProxyPort++;
        Assert.Contains("ProxyPort", Assert.Throws<ClusteringConformanceException>(() =>
            MembershipTableTestRunner.AssertHeartbeat(before, Snapshot(entry))).Message);
    }

    [Fact]
    public void History_RejectsRollbackAndSameVersionIdentityReplacement()
    {
        var entry = CreateEntry(1);
        var history = new MembershipHistory();
        history.Observe(Snapshot(entry));
        Assert.Contains("rollback", Assert.Throws<ClusteringConformanceException>(() =>
            history.Observe(Snapshot(entry, 3, "table-3"))).Message);
        Assert.Contains("same-version drift", Assert.Throws<ClusteringConformanceException>(() =>
            history.Observe(Snapshot(CreateSuccessor(entry)))).Message);
        Assert.Contains("table ETag", Assert.Throws<ClusteringConformanceException>(() =>
            history.Observe(Snapshot(entry, tableToken: "different-token"))).Message);
    }

    [Theory]
    [InlineData(SiloStatus.Active)]
    [InlineData(SiloStatus.Dead)]
    public void History_SameVersionRejectsChangedLiveOrRetainedDeadFields(SiloStatus status)
    {
        var entry = CreateEntry(1, status: status);
        var history = new MembershipHistory();
        history.Observe(Snapshot(entry));
        entry.HostName = "illicit-drift";
        var exception = Assert.Throws<ClusteringConformanceException>(() => history.Observe(Snapshot(entry)));
        Assert.Contains("same-version drift", exception.Message);
        Assert.Contains(".HostName: expected=host-1, observed=illicit-drift", exception.Message);
    }
}
