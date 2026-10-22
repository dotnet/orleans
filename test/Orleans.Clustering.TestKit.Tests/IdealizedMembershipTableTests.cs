using System.Net;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Clustering.TestKit.Tests;

public sealed class IdealizedMembershipTableTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTime Start = new(2024, 5, 6, 7, 8, 0, DateTimeKind.Utc);

    // Hand-authored provider expectations, not the conformance runner or its snapshot oracle.
    private static MembershipEntry Entry(int port = 13001) => new()
    {
        SiloAddress = SiloAddress.New(IPAddress.Loopback, port, 20),
        Status = SiloStatus.Joining,
        HostName = "worker",
        SiloName = "worker-a",
        ProxyPort = 30001,
        RoleName = "role",
        UpdateZone = 2,
        FaultZone = 3,
        StartTime = Start,
        IAmAliveTime = Start.AddSeconds(1),
        SuspectTimes = [Tuple.Create(SiloAddress.New(IPAddress.Loopback, 13002, 21), Start.AddSeconds(2))]
    };

    [Fact]
    public async Task InsertAndUpdate_CurrentTokens_AtomicallyCommitExactValuesAndVersion()
    {
        var backend = new IdealizedMembershipBackend();
        var a = backend.Create("A");
        var b = backend.Create("A");
        var initial = await a.ReadAllAsync(Ct);
        var input = Entry();
        Assert.True(await a.InsertRowAsync(input, initial.Version.Next(), Ct));
        var inserted = await b.ReadAllAsync(Ct);
        var row = Assert.Single(inserted.Members);
        Assert.Equal(initial.Version.Version + 1, inserted.Version.Version);
        Assert.NotEqual(initial.Version.VersionEtag, inserted.Version.VersionEtag);
        Assert.Equal("role", row.Item1.RoleName);
        Assert.Equal(2, row.Item1.UpdateZone);
        Assert.Equal(3, row.Item1.FaultZone);
        input.Status = SiloStatus.Active;
        input.HostName = "active-worker";
        Assert.True(await b.UpdateRowAsync(input, row.Item2, inserted.Version.Next(), Ct));
        var updated = await a.ReadAllAsync(Ct);
        Assert.Equal(inserted.Version.Version + 1, updated.Version.Version);
        Assert.NotEqual(row.Item2, Assert.Single(updated.Members).Item2);
        Assert.Equal(SiloStatus.Active, updated.Members[0].Item1.Status);
        Assert.Equal("active-worker", updated.Members[0].Item1.HostName);
    }

    [Fact]
    public async Task StrongerRowCondition_InvalidIndependentTokens_LeaveAllStateUnchanged()
    {
        var table = new IdealizedMembershipBackend().Create("A");
        var original = await table.ReadAllAsync(Ct);
        var entry = Entry();
        Assert.True(await table.InsertRowAsync(entry, original.Version.Next(), Ct));
        var inserted = await table.ReadAllAsync(Ct);
        var oldRow = inserted.Members[0].Item2;
        entry.Status = SiloStatus.Active;
        Assert.False(await table.UpdateRowAsync(entry, oldRow, original.Version.Next(), Ct));
        Assert.True(await table.UpdateRowAsync(entry, oldRow, inserted.Version.Next(), Ct));
        var current = await table.ReadAllAsync(Ct);
        entry.Status = SiloStatus.Stopping;
        Assert.False(await table.UpdateRowAsync(entry, oldRow, current.Version.Next(), Ct));
        var after = await table.ReadAllAsync(Ct);
        Assert.Equal(current.Version, after.Version);
        Assert.Equal(current.Members[0].Item2, after.Members[0].Item2);
        Assert.Equal(SiloStatus.Active, after.Members[0].Item1.Status);
        Assert.Equal(Start.AddSeconds(1), after.Members[0].Item1.IAmAliveTime);
    }

    [Fact]
    public async Task VersionOnlyCas_PhysicalRowMetadataChangesPreserveOriginalInputsAndAtomicFailures()
    {
        var backend = new IdealizedMembershipBackend { PhysicalRowEtags = true };
        var table = backend.Create("A");
        var entry = Entry();
        Assert.True(await table.InsertRowAsync(entry, (await table.ReadAllAsync(Ct)).Version.Next(), Ct));
        var original = await table.ReadAllAsync(Ct);
        var capturedRowEtag = Assert.Single(original.Members).Item2;
        var reads = backend.Reads;
        await table.UpdateIAmAliveAsync(new() { SiloAddress = entry.SiloAddress, IAmAliveTime = Start.AddMinutes(2) }, Ct);
        Assert.Equal(reads, backend.Reads);
        Assert.Single(backend.HeartbeatWrites);
        Assert.Equal(1, backend.HeartbeatRowMetadataChanges);
        var heartbeat = await table.ReadAllAsync(Ct);
        Assert.Equal(original.Version, heartbeat.Version);
        Assert.NotEqual(capturedRowEtag, Assert.Single(heartbeat.Members).Item2);
        entry.Status = SiloStatus.Active;
        Assert.True(await table.UpdateRowAsync(entry, capturedRowEtag, original.Version.Next(), Ct));
        var committed = await table.ReadAllAsync(Ct);
        Assert.Equal(original.Version.Version + 1, committed.Version.Version);
        Assert.Equal(SiloStatus.Active, Assert.Single(committed.Members).Item1.Status);

        entry.Status = SiloStatus.Stopping;
        Assert.False(await table.UpdateRowAsync(entry, committed.Members[0].Item2, original.Version.Next(), Ct));
        Assert.False(await table.UpdateRowAsync(Entry(13003), capturedRowEtag, committed.Version.Next(), Ct));
        var afterFailures = await table.ReadAllAsync(Ct);
        Assert.Equal(committed.Version, afterFailures.Version);
        Assert.Equal(committed.Members[0].Item1.ToFullString(), Assert.Single(afterFailures.Members).Item1.ToFullString());
        Assert.Equal(committed.Members[0].Item2, afterFailures.Members[0].Item2);
        Assert.Equal(0, backend.RowConditionChecks);
        Assert.Equal(3, backend.VersionedUpdates);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Heartbeat_BlindWritesPreserveTokensWhileFullRowWritesMayClobberLiveness(bool preserveHeartbeatOnFullWrite)
    {
        var backend = new IdealizedMembershipBackend { PreserveHeartbeatOnFullWrite = preserveHeartbeatOnFullWrite };
        var table = backend.Create("A");
        var initial = await table.ReadAllAsync(Ct);
        var entry = Entry();
        Assert.True(await table.InsertRowAsync(entry, initial.Version.Next(), Ct));
        var inserted = await table.ReadAllAsync(Ct);
        var reads = backend.Reads;
        entry.IAmAliveTime = Start.AddMinutes(1);
        await table.UpdateIAmAliveAsync(entry, Ct);
        entry.IAmAliveTime = Start.AddMinutes(2);
        await table.UpdateIAmAliveAsync(entry, Ct);
        await table.UpdateIAmAliveAsync(entry, Ct);
        Assert.Equal(reads, backend.Reads);
        Assert.Equal(0, backend.VersionedUpdates);
        Assert.Equal(new[] { Start.AddMinutes(1), Start.AddMinutes(2), Start.AddMinutes(2) }, backend.HeartbeatWrites.Select(write => write.Time));
        Assert.All(backend.HeartbeatWrites, write => Assert.Same(table, write.Owner));
        var heartbeat = await table.ReadAllAsync(Ct);
        Assert.Equal(inserted.Version, heartbeat.Version);
        Assert.Equal(Start.AddMinutes(2), heartbeat.Members[0].Item1.IAmAliveTime);
        Assert.Equal(inserted.Members[0].Item2, heartbeat.Members[0].Item2);
        entry.Status = SiloStatus.Active;
        entry.IAmAliveTime = Start;
        Assert.True(await table.UpdateRowAsync(entry, inserted.Members[0].Item2, inserted.Version.Next(), Ct));
        var final = await table.ReadAllAsync(Ct);
        Assert.Equal(preserveHeartbeatOnFullWrite ? Start.AddMinutes(2) : Start, final.Members[0].Item1.IAmAliveTime);
        Assert.Equal(SiloStatus.Active, final.Members[0].Item1.Status);
        Assert.Equal(heartbeat.Version.Version + 1, final.Version.Version);
    }

    [Fact]
    public async Task Heartbeat_MissingRow_PreservesNativeError()
    {
        var backend = new IdealizedMembershipBackend();
        var table = backend.Create("A");
        await table.InitializeMembershipTableAsync(true, Ct);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => table.UpdateIAmAliveAsync(Entry(), Ct));

        Assert.Empty(backend.HeartbeatWrites);
        Assert.Equal(0, backend.Reads);
    }

    [Fact]
    public async Task ReadsAndWrites_DetachNestedMutableValues()
    {
        var table = new IdealizedMembershipBackend().Create("A");
        var input = Entry();
        Assert.True(await table.InsertRowAsync(input, (await table.ReadAllAsync(Ct)).Version.Next(), Ct));
        input.SuspectTimes!.Clear();
        input.HostName = "caller";
        var retained = await table.ReadAllAsync(Ct);
        Assert.Equal("worker", retained.Members[0].Item1.HostName);
        Assert.Single(retained.Members[0].Item1.SuspectTimes!);
        var candidate = Entry();
        candidate.Status = SiloStatus.Active;
        Assert.True(await table.UpdateRowAsync(candidate, retained.Members[0].Item2, retained.Version.Next(), Ct));
        candidate.SuspectTimes![0] = Tuple.Create(candidate.SiloAddress, Start.AddDays(1));
        Assert.Equal(SiloStatus.Joining, retained.Members[0].Item1.Status);
        var read = await table.ReadAllAsync(Ct);
        read.Members[0].Item1.SuspectTimes!.Clear();
        Assert.Equal(Start.AddSeconds(2), Assert.Single((await table.ReadAllAsync(Ct)).Members[0].Item1.SuspectTimes!).Item2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleanup_UsesDeadOnlyAndStrictEffectiveTimeCutoff(bool versioned)
    {
        var table = new IdealizedMembershipBackend { VersionedCleanup = versioned }.Create("A");
        foreach (var (status, port, vote) in new[] { (SiloStatus.Stopping, 13001, Start), (SiloStatus.Dead, 13002, Start),
            (SiloStatus.Dead, 13003, Start.AddMinutes(1)), (SiloStatus.Dead, 13004, Start.AddMinutes(2)) })
        {
            var entry = Entry(port);
            entry.Status = status;
            entry.SuspectTimes![0] = Tuple.Create(entry.SiloAddress, vote);
            Assert.True(await table.InsertRowAsync(entry, (await table.ReadAllAsync(Ct)).Version.Next(), Ct));
        }
        var before = await table.ReadAllAsync(Ct);
        await table.CleanupDefunctSiloEntriesAsync(new(Start.AddMinutes(1)), Ct);
        var after = await table.ReadAllAsync(Ct);
        Assert.Equal(before.Version.Version + (versioned ? 1 : 0), after.Version.Version);
        Assert.Equal(versioned, before.Version.VersionEtag != after.Version.VersionEtag);
        Assert.Equal(new[] { 13001, 13003, 13004 }, after.Members.Select(r => r.Item1.SiloAddress.Endpoint.Port).Order());
        Assert.All(after.Members, row => Assert.Equal(before.TryGet(row.Item1.SiloAddress)!.Item2, row.Item2));
        await table.CleanupDefunctSiloEntriesAsync(new(Start.AddMinutes(1)), Ct);
        var repeated = await table.ReadAllAsync(Ct);
        Assert.Equal(after.Version, repeated.Version);
        Assert.Equal(after.Members.Select(row => row.Item2), repeated.Members.Select(row => row.Item2));
        await table.CleanupDefunctSiloEntriesAsync(new(Start.AddMinutes(1).AddTicks(1)), Ct);
        var boundaryRemoved = await table.ReadAllAsync(Ct);
        Assert.Equal(repeated.Version.Version + (versioned ? 1 : 0), boundaryRemoved.Version.Version);
        Assert.Equal(versioned, repeated.Version.VersionEtag != boundaryRemoved.Version.VersionEtag);
        Assert.Equal(new[] { 13001, 13004 }, boundaryRemoved.Members.Select(row => row.Item1.SiloAddress.Endpoint.Port).Order());
        Assert.All(boundaryRemoved.Members, row =>
        {
            var original = repeated.TryGet(row.Item1.SiloAddress)!;
            Assert.Equal(original.Item1.ToFullString(), row.Item1.ToFullString());
            Assert.Equal(original.Item2, row.Item2);
        });
    }

    [Fact]
    public async Task Initialize_NonemptyStateIsPreserved()
    {
        var table = new IdealizedMembershipBackend().Create("A");
        Assert.True(await table.InsertRowAsync(Entry(), (await table.ReadAllAsync(Ct)).Version.Next(), Ct));
        var before = await table.ReadAllAsync(Ct);
        await table.InitializeMembershipTableAsync(true, Ct);
        await table.InitializeMembershipTableAsync(false, Ct);
        var after = await table.ReadAllAsync(Ct);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.Members[0].Item2, Assert.Single(after.Members).Item2);
        Assert.Equal("worker", after.Members[0].Item1.HostName);
    }

    [Fact]
    public async Task Handles_AreDistinctAndShareOnlyTheirAssignedCluster()
    {
        var backend = new IdealizedMembershipBackend();
        var a = backend.Create("A");
        var peer = backend.Create("A");
        var b = backend.Create("B");
        Assert.NotSame(a, peer);
        Assert.True(await a.InsertRowAsync(Entry(), (await a.ReadAllAsync(Ct)).Version.Next(), Ct));
        Assert.Single((await peer.ReadAllAsync(Ct)).Members);
        Assert.Empty((await b.ReadAllAsync(Ct)).Members);
        Assert.Equal((await a.ReadAllAsync(Ct)).Version, (await peer.ReadAllAsync(Ct)).Version);
    }

    [Fact]
    public async Task Delete_UsesRequestedClusterAndPreservesOtherPartitions()
    {
        var backend = new IdealizedMembershipBackend();
        var a = backend.Create("A");
        var b = backend.Create("B");
        Assert.True(await a.InsertRowAsync(Entry(), (await a.ReadAllAsync(Ct)).Version.Next(), Ct));
        Assert.True(await b.InsertRowAsync(Entry(), (await b.ReadAllAsync(Ct)).Version.Next(), Ct));
        var before = await a.ReadAllAsync(Ct);
        await a.DeleteMembershipTableEntriesAsync("B", Ct);
        await b.DeleteMembershipTableEntriesAsync("unused", Ct);
        Assert.Empty((await b.ReadAllAsync(Ct)).Members);
        Assert.Equal(before.Version, (await a.ReadAllAsync(Ct)).Version);
        Assert.Equal("worker", Assert.Single((await a.ReadAllAsync(Ct)).Members).Item1.HostName);
    }
}
