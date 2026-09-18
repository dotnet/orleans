using Orleans.Runtime;
using Xunit;
using static Orleans.Clustering.TestKit.MembershipTableTestData;

namespace Orleans.Clustering.TestKit.Tests;

public sealed class MembershipTableModelTests
{
    private static void Apply(MembershipModelState state, MembershipOperationKind kind, int key = 1)
    {
        var request = new MembershipRequest(kind, key);
        Assert.True(MembershipModel.CanApply(request, state), request.ToString());
        MembershipModel.Apply(request, state);
    }

    [Fact]
    public void Apply_InsertAndForwardUpdate_AdvanceModelExactlyOnce()
    {
        var state = new MembershipModelState();
        Apply(state, MembershipOperationKind.InsertNew);
        Apply(state, MembershipOperationKind.InsertNew, 2);
        Apply(state, MembershipOperationKind.UpdateForward);
        Assert.Equal(3, state.Version);
        Assert.Equal((int)SiloStatus.Joining, state.Rows[1].Status);
        Assert.Equal("host-1-updated", state.Rows[1].HostName);
        Assert.Equal(22002, state.Rows[1].ProxyPort);
        Assert.Equal((int)SiloStatus.Created, state.Rows[2].Status);
        Assert.Equal("host-2", state.Rows[2].HostName);
        Assert.Equal(1, state.LastChangedKey);
    }

    [Fact]
    public void Apply_StaleTableAndStaleRow_AreIndependentAndSideEffectFree()
    {
        var state = new MembershipModelState();
        Apply(state, MembershipOperationKind.InsertNew);
        Assert.False(MembershipModel.CanApply(new(MembershipOperationKind.UpdateStaleRow), state));
        Apply(state, MembershipOperationKind.InsertNew, 2);
        Apply(state, MembershipOperationKind.UpdateStaleTable);
        Assert.Equal(2, state.Version);
        Assert.Equal(1, state.Rows[1].Revision);
        Apply(state, MembershipOperationKind.UpdateForward);
        Assert.False(MembershipModel.CanApply(new(MembershipOperationKind.UpdateStaleTable), state));
        Apply(state, MembershipOperationKind.UpdateStaleRow);
        Assert.Equal(3, state.Version);
        Assert.Equal(2, state.Rows[1].Revision);
        Assert.Equal(T0.Ticks, state.Rows[1].HeartbeatTicks);
    }

    [Fact]
    public void Apply_HeartbeatAndOldHeartbeatStatusUpdate_PreserveMaximum()
    {
        var state = new MembershipModelState();
        Apply(state, MembershipOperationKind.InsertNew);
        Apply(state, MembershipOperationKind.HeartbeatNewer);
        Apply(state, MembershipOperationKind.HeartbeatOlder);
        Assert.Equal(1, state.Version);
        Assert.Equal(T2.Ticks, state.Rows[1].HeartbeatTicks);
        Apply(state, MembershipOperationKind.UpdateWithOldHeartbeat);
        Assert.Equal(2, state.Version);
        Assert.Equal((int)SiloStatus.Joining, state.Rows[1].Status);
        Assert.Equal(T2.Ticks, state.Rows[1].HeartbeatTicks);
    }

    [Fact]
    public void CanApply_RejectsBackwardLifecycleAndCompactedIdentityReuse()
    {
        var state = new MembershipModelState();
        Apply(state, MembershipOperationKind.InsertNew);
        for (var i = 0; i < 5; i++) Apply(state, MembershipOperationKind.UpdateForward);
        Assert.Equal((int)SiloStatus.Dead, state.Rows[1].Status);
        Assert.False(MembershipModel.CanApply(new(MembershipOperationKind.UpdateForward), state));
        Apply(state, MembershipOperationKind.CleanupDead);
        Assert.False(MembershipModel.CanApply(new(MembershipOperationKind.InsertNew), state));
        Assert.True(MembershipModel.CanApply(new(MembershipOperationKind.StartSuccessor), state));
        Assert.Equal(0, state.TerminalGenerations[1]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Apply_CleanupRemovesOnlyEligibleDeadAndKeepsTerminalLedger(int versionDelta)
    {
        var state = new MembershipModelState();
        Apply(state, MembershipOperationKind.InsertNew);
        Apply(state, MembershipOperationKind.InsertNew, 2);
        for (var i = 0; i < 5; i++) Apply(state, MembershipOperationKind.UpdateForward);
        MembershipModel.Apply(new(MembershipOperationKind.CleanupDead), state, versionDelta);
        Assert.Single(state.Rows);
        Assert.Equal((int)SiloStatus.Created, state.Rows[2].Status);
        Assert.Equal(7 + versionDelta, state.Version);
        Assert.Equal(0, state.TerminalGenerations[1]);
    }

    [Fact]
    public void Apply_SuccessorUsesSameEndpointAndNewGeneration()
    {
        var state = new MembershipModelState();
        Apply(state, MembershipOperationKind.InsertNew);
        var predecessor = state.Rows[1].ToEntry(7);
        for (var i = 0; i < 5; i++) Apply(state, MembershipOperationKind.UpdateForward);
        Apply(state, MembershipOperationKind.CleanupDead);
        Apply(state, MembershipOperationKind.StartSuccessor);
        var successor = state.Rows[1].ToEntry(7);
        Assert.Equal(predecessor.SiloAddress.Endpoint, successor.SiloAddress.Endpoint);
        Assert.Equal(predecessor.SiloAddress.Generation + 1, successor.SiloAddress.Generation);
        Assert.Equal(SiloStatus.Created, successor.Status);
        Assert.Equal(T2, successor.IAmAliveTime);
        Assert.Equal(7, state.Version);
        Assert.Equal(0, state.TerminalGenerations[1]);
    }

    [Fact]
    public void Apply_CleanupWithoutEligibleRows_PreservesVersionAndDeadRecord()
    {
        var state = new MembershipModelState();
        Apply(state, MembershipOperationKind.InsertNew);
        Apply(state, MembershipOperationKind.HeartbeatNewer);
        for (var i = 0; i < 5; i++) Apply(state, MembershipOperationKind.UpdateForward);
        var version = state.Version;

        Apply(state, MembershipOperationKind.CleanupDead);

        Assert.Equal(version, state.Version);
        Assert.Equal((int)SiloStatus.Dead, Assert.Single(state.Rows).Value.Status);
        Assert.Equal(T2.Ticks, state.Rows[1].HeartbeatTicks);
        Assert.Equal(0, state.TerminalGenerations[1]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void Apply_CleanupRejectsIllegalVersionDeltaBeforeRemovingRows(int versionDelta)
    {
        var state = new MembershipModelState();
        Apply(state, MembershipOperationKind.InsertNew);
        for (var i = 0; i < 5; i++) Apply(state, MembershipOperationKind.UpdateForward);
        var failure = Assert.Throws<ClusteringConformanceException>(() =>
            MembershipModel.Apply(new(MembershipOperationKind.CleanupDead), state, versionDelta));
        Assert.Contains("model cleanup version", failure.Message);
        Assert.Equal(6, state.Version);
        Assert.Equal(6, state.Steps);
        Assert.Equal((int)SiloStatus.Dead, Assert.Single(state.Rows).Value.Status);
        Assert.Equal(0, state.TerminalGenerations[1]);
    }

    [Fact]
    public void Apply_AdministrativeDeletionEndsEveryOperationInThatHistory()
    {
        var state = new MembershipModelState();
        Apply(state, MembershipOperationKind.InsertNew);
        for (var i = 0; i < 5; i++) Apply(state, MembershipOperationKind.UpdateForward);
        Apply(state, MembershipOperationKind.DeleteCluster);
        Assert.Empty(state.Rows);
        Assert.Equal(6, state.Version);
        Assert.True(state.Deleted);
        Assert.Equal(0, state.TerminalGenerations[1]);
        foreach (var kind in Enum.GetValues<MembershipOperationKind>())
        {
            Assert.False(MembershipModel.CanApply(new(kind), state));
            Assert.Contains("history ended", Assert.Throws<ClusteringConformanceException>(() =>
                MembershipModel.Apply(new(kind), state)).Message);
        }
        var fresh = new MembershipModelState();
        Apply(fresh, MembershipOperationKind.InsertNew);
        Assert.Equal(1, fresh.Version);
        Assert.Equal((int)SiloStatus.Created, Assert.Single(fresh.Rows).Value.Status);
        Assert.True(state.Deleted);
    }

    [Fact]
    public void Apply_ReadHistoryAcceptsSkippedVersionsButRejectsSameVersionDrift()
    {
        var history = new MembershipHistory();
        var entry = CreateEntry(1);
        history.Observe(ClusteringMembershipSnapshot.Capture(new(Tuple.Create(entry, "r1"), new(1, "v1"))));
        entry.Status = SiloStatus.Active;
        history.Observe(ClusteringMembershipSnapshot.Capture(new(Tuple.Create(entry, "r3"), new(3, "v3"))));
        entry.ProxyPort++;
        var failure = Assert.Throws<ClusteringConformanceException>(() =>
            history.Observe(ClusteringMembershipSnapshot.Capture(new(Tuple.Create(entry, "r3"), new(3, "v3")))));
        Assert.Contains("ProxyPort", failure.Message);
        Assert.Contains("same-version drift", failure.Message);
    }

    [Fact]
    public void CreateInputSet_ContainsEveryRequiredOperationAndReachableTokenMode()
    {
        var spec = new MembershipBehavioralSpec();
        var inputs = spec.CreateInputSet();
        Assert.Equal(Enum.GetValues<MembershipOperationKind>(), inputs.Inputs.Select(i => ((MembershipRequest)i.Request).Kind).Distinct().Order());
        Assert.Equal(36, inputs.Inputs.Count());
        var reached = new HashSet<MembershipOperationKind>();
        foreach (var prefix in MembershipTableModelBasedTestRunner.RequiredPrefixes())
        {
            var state = new MembershipModelState();
            foreach (var request in prefix)
            {
                Assert.True(MembershipModel.CanApply(request, state), request.ToString());
                MembershipModel.Apply(request, state);
                reached.Add(request.Kind);
            }
        }
        Assert.Contains(MembershipOperationKind.UpdateStaleTable, reached);
        Assert.Contains(MembershipOperationKind.UpdateStaleRow, reached);
        Assert.Contains(MembershipOperationKind.UpdateWithOldHeartbeat, reached);
        Assert.Contains(MembershipOperationKind.StartSuccessor, reached);
    }
}
