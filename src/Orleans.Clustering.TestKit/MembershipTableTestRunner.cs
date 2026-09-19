using System.Runtime.CompilerServices;
using Orleans.Runtime;
using static Orleans.Clustering.TestKit.MembershipTableTestData;

namespace Orleans.Clustering.TestKit;

/// <summary>Sealed, unconditional, individually invocable membership table guarantees.</summary>
/// <remarks>Use a fresh initialized fixture for each method. Failed assertions and infrastructure errors propagate to the caller.</remarks>
public sealed class MembershipTableTestRunner
{
    private readonly MembershipTableTestFixture _fixture;
    private readonly int _seed;
    private readonly Action<string>? _output;
    private readonly int _concurrencyRowCount;
    private IMembershipTable A => _fixture.First;
    private IMembershipTable B => _fixture.Second;
    private IMembershipTable Other => _fixture.OtherCluster;

    /// <summary>Creates a runner over initialized independent handles. Row count controls workload, never guarantees.</summary>
    public MembershipTableTestRunner(MembershipTableTestFixture fixture, int seed = 0, Action<string>? output = null, int concurrencyRowCount = 128)
    {
        _fixture = fixture ?? throw new ArgumentNullException(nameof(fixture));
        if (fixture.First is null || fixture.Second is null || fixture.OtherCluster is null)
            throw new ArgumentException("Initialize the fixture before constructing its runner.", nameof(fixture));
        if (concurrencyRowCount is < 3 or > 10000) throw new ArgumentOutOfRangeException(nameof(concurrencyRowCount));
        _seed = seed;
        _output = output;
        _concurrencyRowCount = concurrencyRowCount;
    }

    /// <summary>G01: insertion and its version are one exact +1 commit, preserving the sentinel.</summary>
    public Task InsertRow_CurrentTableVersion_CommitsExactlyOneVersion(CancellationToken cancellationToken = default) => Run(async ct =>
    {
        await Insert(A, Entry(1), ct);
        await Insert(B, Entry(2), ct);
        await SameHandles(ct);
    }, cancellationToken);

    /// <summary>G02: a current-token forward update replaces exactly its intended row.</summary>
    public Task UpdateRow_CurrentTokens_CommitsExactlyOneVersion(CancellationToken cancellationToken = default) => Run(async ct =>
    {
        await Seed(ct);
        var updated = Forward(Entry(1));
        updated.IAmAliveTime = T2;
        updated.SuspectTimes![0] = Tuple.Create(updated.SuspectTimes[0].Item1, T1);
        await Update(B, updated, ct);
        await SameHandles(ct);
        updated = Forward(updated);
        updated.SuspectTimes = [];
        await Update(A, updated, ct);
        await SameHandles(ct);
        updated = Forward(updated);
        await Update(B, updated, ct);
        updated = Forward(updated);
        updated.SuspectTimes = null;
        await Update(A, updated, ct);
        await SameHandles(ct);
    }, cancellationToken);

    /// <summary>G03: equal versions retain canonical fields and non-Dead rows while liveness varies and Dead rows compact.</summary>
    public Task Reads_SameVersion_PreservesRetainedCanonicalFields(CancellationToken cancellationToken = default) => Run(async ct =>
    {
        await Seed(ct);
        var dead = Entry(3, SiloStatus.Dead);
        await Insert(A, dead, ct);
        var history = new MembershipHistory();
        history.Observe(await Read(A, ct));
        history.Observe(await Read(B, ct));
        await Heartbeat(B, Entry(1), T2, ct);
        var beforeCleanup = await Read(A, ct);
        history.Observe(beforeCleanup);
        await B.CleanupDefunctSiloEntriesAsync(new(T1), ct);
        var compacted = await Read(A, ct);
        AssertCleanup(beforeCleanup, compacted, T1);
        history.Observe(compacted);
        Check(!compacted.Rows.ContainsKey(dead.SiloAddress.ToParsableString()), "old Dead was not compacted");
        Check(history.IsTerminal(dead.SiloAddress.ToParsableString()), "terminal history lost during compaction");
        history.Observe(await Read(B, ct));
    }, cancellationToken);

    /// <summary>G04: reads may skip two separately validated commits.</summary>
    public Task Reads_MaySkipCommittedVersions_WithoutSkippingHistoryValidation(CancellationToken cancellationToken = default) => Run(async ct =>
    {
        await Seed(ct);
        var initial = await Read(A, ct);
        var history = new MembershipHistory();
        history.Observe(initial);
        await Update(B, Forward(Entry(1)), ct);
        var final = await Update(B, Forward(Entry(2)), ct);
        var observed = await Read(A, ct);
        Check(observed.Version == initial.Version + 2, $"expected skipped read version={initial.Version + 2}, observed={observed.Version}");
        Equal(final, observed);
        history.Observe(observed);
    }, cancellationToken);

    /// <summary>G05: legal lifecycle, terminal ledger, and a distinct successor generation.</summary>
    public Task Lifecycle_DeadRemainsTerminalAfterCompaction_SuccessorUsesNewGeneration(CancellationToken cancellationToken = default) => Run(async ct =>
    {
        var entry = Entry(1);
        var history = new MembershipHistory();
        history.Observe(await Insert(A, entry, ct));
        while (entry.Status != SiloStatus.Dead)
        {
            entry = Forward(entry);
            history.Observe(await Update(B, entry, ct));
        }

        var beforeCleanup = await SameHandles(ct);
        await A.CleanupDefunctSiloEntriesAsync(new(T1), ct);
        var compacted = await Read(B, ct);
        AssertCleanup(beforeCleanup, compacted, T1);
        history.Observe(compacted);
        Check(compacted.Rows.Count == 0, "Dead predecessor must compact");
        var successor = CreateSuccessor(entry);
        Check(successor.SiloAddress.Generation > entry.SiloAddress.Generation
            && successor.SiloAddress.Endpoint.Equals(entry.SiloAddress.Endpoint), "successor must have same endpoint and greater generation");
        history.Observe(await Insert(B, successor, ct));
        var final = await SameHandles(ct);
        Check(!final.Rows.ContainsKey(entry.SiloAddress.ToParsableString()), "compacted predecessor reappeared");
        Check(history.IsTerminal(entry.SiloAddress.ToParsableString()), "predecessor terminality was forgotten");
        EqualRow(MembershipEntrySnapshot.Capture(successor), final.Row(successor.SiloAddress).Entry);
    }, cancellationToken);

    /// <summary>G06: owner liveness writes preserve canonical membership fields and the table version and ETag.</summary>
    public Task UpdateIAmAlive_OwnerWrites_PreserveCanonicalFieldsAndTableVersion(CancellationToken cancellationToken = default) => Run(async ct =>
    {
        await Seed(ct);
        await Heartbeat(A, Entry(1), T1, ct);
        await Heartbeat(A, Entry(1), T2, ct);
        await Heartbeat(A, Entry(1), T2, ct);
        await SameHandles(ct);
    }, cancellationToken);

    /// <summary>G07: tokens captured before an owner heartbeat commit a canonical status change exactly once.</summary>
    public Task UpdateRow_TokensCapturedBeforeHeartbeat_CommitStatusChange(CancellationToken cancellationToken = default)
        => Run(ct => UpdateAfterHeartbeat(updateStatus: true, ct), cancellationToken);

    /// <summary>G08: tokens captured before an owner heartbeat commit a canonical suspect-vote change exactly once.</summary>
    public Task UpdateRow_TokensCapturedBeforeHeartbeat_CommitVoteChange(CancellationToken cancellationToken = default)
        => Run(ct => UpdateAfterHeartbeat(updateStatus: false, ct), cancellationToken);

    private async Task UpdateAfterHeartbeat(bool updateStatus, CancellationToken ct)
    {
        await Seed(ct);
        var before = await SameHandles(ct);
        var payload = updateStatus ? Forward(Entry(1)) : Entry(1);
        if (!updateStatus)
        {
            payload.AddOrUpdateSuspector(Entry(2).SiloAddress, T1, maxVotes: 10);
        }

        await B.UpdateIAmAliveAsync(new MembershipEntry { SiloAddress = payload.SiloAddress, IAmAliveTime = T2 }, ct);
        Check(await A.UpdateRowAsync(payload, before.Row(payload.SiloAddress).Etag, before.Next(), ct),
            "canonical update with pre-heartbeat tokens returned false");
        AssertCommit(before, await SameHandles(ct), payload);
    }

    /// <summary>G09: distinct constructed handles share one committed backing table.</summary>
    public Task Handles_IndependentlyConstructed_ShareCommittedBackingState(CancellationToken cancellationToken = default) => Run(async ct =>
    {
        Check(!ReferenceEquals(A, B), "two independently constructed IMembershipTable instances required");
        var first = await Insert(A, Entry(1), ct);
        Equal(first, await Read(B, ct));
        var second = await Insert(B, Entry(2), ct);
        Equal(second, await Read(A, ct));
    }, cancellationToken);

    /// <summary>G10: stale-table insertion has no side effects.</summary>
    public Task InsertRow_StaleTableToken_ReturnsFalseWithoutSideEffects(CancellationToken cancellationToken = default) => Run(async ct =>
    {
        await Insert(A, Entry(1), ct);
        var stale = (await Read(A, ct)).Next();
        await Insert(B, Entry(2), ct);
        await Reject(() => A.InsertRowAsync(Entry(3), stale, ct), ct);
    }, cancellationToken);

    /// <summary>G11: a stale table token is rejected even with freshly read target-row metadata.</summary>
    public Task UpdateRow_StaleTableTokenWithCurrentRowToken_ReturnsFalseWithoutSideEffects(CancellationToken cancellationToken = default) => Run(async ct =>
    {
        await Seed(ct);
        var stale = await Read(A, ct);
        await Update(B, Forward(Entry(2)), ct);
        var current = await Read(A, ct);
        var rowEtag = current.Row(Entry(1).SiloAddress).Etag;
        Check(stale.TableEtag != current.TableEtag,
            "stale-table arrangement must advance the table token while using the freshly read target row token");
        var rejected = Forward(Entry(1));
        rejected.IAmAliveTime = T2;
        await Reject(() => A.UpdateRowAsync(rejected, rowEtag, stale.Next(), ct), ct);
    }, cancellationToken);

    /// <summary>G12: a snapshot preceding a commit to the same row is rejected without partial effects.</summary>
    public Task UpdateRow_StaleSnapshotAfterSameRowCommit_ReturnsFalseWithoutSideEffects(CancellationToken cancellationToken = default) => Run(async ct =>
    {
        await Seed(ct);
        var stale = await Read(A, ct);
        var joining = Forward(Entry(1));
        var current = await Update(B, joining, ct);
        Check(stale.TableEtag != current.TableEtag, "arrangement did not produce a stale table token");
        var rejected = Forward(joining);
        rejected.IAmAliveTime = T2;
        await Reject(() => A.UpdateRowAsync(rejected, stale.Row(joining.SiloAddress).Etag, stale.Next(), ct), ct);
    }, cancellationToken);

    /// <summary>G13: duplicate identity with a current valid candidate is rejected atomically.</summary>
    public Task InsertRow_DuplicateIdentity_ReturnsFalseWithoutSideEffects(CancellationToken cancellationToken = default) => Run(async ct =>
    {
        await Seed(ct);
        var current = await Read(B, ct);
        var rejected = Forward(Entry(1));
        rejected.IAmAliveTime = T2;
        await Reject(() => A.InsertRowAsync(rejected, current.Next(), ct), ct);
    }, cancellationToken);

    /// <summary>G14: a real row token cannot create a missing row through UpdateRow.</summary>
    public Task UpdateRow_MissingIdentityWithRealToken_ReturnsFalseWithoutSideEffects(CancellationToken cancellationToken = default) => Run(async ct =>
    {
        await Seed(ct);
        var current = await Read(B, ct);
        var rejected = Entry(3);
        rejected.IAmAliveTime = T2;
        await Reject(() => A.UpdateRowAsync(rejected, current.Row(Entry(1).SiloAddress).Etag, current.Next(), ct), ct);

        var dead = Entry(4, SiloStatus.Active);
        var beforeDeath = await Insert(A, dead, ct);
        dead.Status = SiloStatus.Dead;
        var beforeCleanup = await Update(B, dead, ct);
        await Reject(() => A.UpdateRowAsync(dead, beforeDeath.Row(dead.SiloAddress).Etag, beforeDeath.Next(), ct), ct);
        var delayedRowEtag = beforeCleanup.Row(dead.SiloAddress).Etag;
        var delayedVersion = beforeCleanup.Next();
        await B.CleanupDefunctSiloEntriesAsync(new(T1), ct);
        var compacted = await SameHandles(ct);
        AssertCleanup(beforeCleanup, compacted, T1);
        await Reject(() => A.UpdateRowAsync(dead, delayedRowEtag, delayedVersion, ct), ct);
        await Reject(() => A.UpdateRowAsync(dead, delayedRowEtag, compacted.Next(), ct), ct);
    }, cancellationToken);

    /// <summary>G15: point/full reads agree for distinct generations, other endpoints, and absent identities.</summary>
    public Task ReadRow_AndReadAll_AgreeForPresentAndAbsentIdentities(CancellationToken cancellationToken = default) => Run(async ct =>
    {
        await Seed(ct);
        var successor = CreateSuccessor(Entry(1));
        await Insert(B, successor, ct);
        var all = await SameHandles(ct);
        Check(all.Rows.Count == 3, $"expected three complete identities, observed={all.Rows.Count}");
        foreach (var entry in new[] { Entry(1), Entry(2), successor, Entry(3) })
        {
            Equal(all.Select(entry.SiloAddress), ClusteringMembershipSnapshot.Capture(await B.ReadRowAsync(entry.SiloAddress, ct)));
        }
    }, cancellationToken);

    /// <summary>G16: the actual retained data, entries, and nested lists remain unchanged after later writes.</summary>
    public Task Reads_RetainedObjectsRemainUnchangedAfterLaterWrites(CancellationToken cancellationToken = default) => Run(async ct =>
    {
        await Seed(ct);
        var retained = await A.ReadAllAsync(ct);
        var row = retained.TryGet(Entry(1).SiloAddress)!;
        var entryReference = row.Item1;
        var listReference = entryReference.SuspectTimes;
        var listBaseline = listReference?.Select(v => new SuspectSnapshot(v.Item1.ToParsableString(), v.Item2)).ToArray() ?? [];
        var baseline = ClusteringMembershipSnapshot.Capture(retained);
        await Heartbeat(B, Entry(1), T2, ct);
        await Update(A, Forward(Entry(1)), ct);
        await Update(B, Forward(Entry(2)), ct);
        Equal(baseline, ClusteringMembershipSnapshot.Capture(retained), complete: true);
        EqualRow(baseline.Row(Entry(1).SiloAddress).Entry, MembershipEntrySnapshot.Capture(entryReference), complete: true);
        Check(listBaseline.SequenceEqual(listReference?.Select(v => new SuspectSnapshot(v.Item1.ToParsableString(), v.Item2)) ?? []),
            "retained actual suspect-list reference changed after provider writes");
    }, cancellationToken);

    /// <summary>G17: inserted caller entry and nested suspect-list aliases cannot mutate storage.</summary>
    public Task InsertRow_MutatingInputAndSuspectList_DoesNotMutateStoredState(CancellationToken cancellationToken = default) => Run(async ct =>
    {
        await Insert(A, Entry(2), ct);
        var input = Entry(1);
        var committed = await Insert(A, input, ct);
        MutateCaller(input);
        Equal(committed, await SameHandles(ct));
    }, cancellationToken);

    /// <summary>G18: updated caller entry and nested suspect-list aliases cannot mutate storage.</summary>
    public Task UpdateRow_MutatingInputAndSuspectList_DoesNotMutateStoredState(CancellationToken cancellationToken = default) => Run(async ct =>
    {
        await Seed(ct);
        var input = Forward(Entry(1));
        var committed = await Update(A, input, ct);
        MutateCaller(input);
        Equal(committed, await SameHandles(ct));
    }, cancellationToken);

    /// <summary>G19: caller mutations of point and full read results cannot mutate storage.</summary>
    public Task Reads_MutatingReturnedEntryAndSuspectList_DoesNotMutateStoredState(CancellationToken cancellationToken = default) => Run(async ct =>
    {
        await Seed(ct);
        var baseline = await Read(B, ct);
        var all = await A.ReadAllAsync(ct);
        MutateCaller(all.TryGet(Entry(1).SiloAddress)!.Item1);
        Equal(baseline, await Read(B, ct));
        var point = await B.ReadRowAsync(Entry(2).SiloAddress, ct);
        MutateCaller(point.Members[0].Item1);
        Equal(baseline, await SameHandles(ct));
    }, cancellationToken);

    /// <summary>G20: two armed cross-row writes using one table candidate have exactly one winner.</summary>
    public Task ConcurrentCrossRowUpdates_SharedTableVersion_HaveExactlyOneWinner(CancellationToken cancellationToken = default) => Run(async ct =>
    {
        await Seed(ct);
        var before = await SameHandles(ct);
        var first = Forward(Entry(1));
        var second = Forward(Entry(2));
        var start = Gate();
        var readyA = Gate();
        var readyB = Gate();
        async Task<bool> Write(IMembershipTable table, MembershipEntry entry, TaskCompletionSource ready)
        {
            ready.SetResult();
            await start.Task.WaitAsync(ct);
            return await table.UpdateRowAsync(entry, before.Row(entry.SiloAddress).Etag, before.Next(), ct);
        }

        var writes = new[] { Write(A, first, readyA), Write(B, second, readyB) };
        await Task.WhenAll(readyA.Task, readyB.Task).WaitAsync(ct);
        start.SetResult();
        var results = await Task.WhenAll(writes).WaitAsync(ct);
        Check(results.Count(success => success) == 1, $"exactly one winner required; observed=[{string.Join(",", results)}]");
        AssertCommit(before, await SameHandles(ct), results[0] ? first : second);
    }, cancellationToken);

    /// <summary>G21: bounded simultaneous full reads match only complete before/after committed views.</summary>
    public Task ConcurrentReadAll_ReturnsOnlyAtomicCommittedViews(CancellationToken cancellationToken = default)
        => Run(ct => ConcurrentReads(pointReads: false, ct), cancellationToken);

    /// <summary>G22: each changing, sentinel, or missing point read matches its own legal atomic view.</summary>
    public Task ConcurrentReadRow_ReturnsOnlyAtomicCommittedViews(CancellationToken cancellationToken = default)
        => Run(ct => ConcurrentReads(pointReads: true, ct), cancellationToken);

    /// <summary>G23: repeated initialization on existing and new handles preserves nonempty state exactly.</summary>
    public Task InitializeMembershipTable_RepeatedWithData_PreservesCommittedState(CancellationToken cancellationToken = default) => Run(async ct =>
    {
        await Seed(ct);
        await Update(B, Forward(Entry(1)), ct);
        await Heartbeat(A, Entry(1), T2, ct);
        var baseline = await SameHandles(ct);
        var additional = await _fixture.CreateAdditionalHandleAsync(_fixture.ClusterId, ct);
        foreach (var table in new[] { A, additional.Table })
        {
            foreach (var initializeVersion in new[] { true, false })
            {
                await table.InitializeMembershipTableAsync(initializeVersion, ct);
                Equal(baseline, await Read(table, ct));
                Equal(baseline, await SameHandles(ct));
            }
        }
    }, cancellationToken);

    /// <summary>G24: shared-backend overlapping identities are isolated by cluster, not service or address.</summary>
    public Task Clusters_SharedBackendWithOverlappingSiloAddresses_AreIsolated(CancellationToken cancellationToken = default) => Run(async ct =>
    {
        await Seed(ct);
        var a = await SameHandles(ct);
        var otherEntry = Entry(1);
        otherEntry.HostName = "other-cluster-host";
        var other = await Insert(Other, otherEntry, ct);
        Equal(a, await SameHandles(ct));
        await Heartbeat(B, Entry(1), T2, ct);
        await Update(A, Forward(Entry(1)), ct);
        await A.InitializeMembershipTableAsync(true, ct);
        Equal(other, await Read(Other, ct));
        a = await SameHandles(ct);
        await Heartbeat(Other, otherEntry, T2, ct);
        await Update(Other, Forward(otherEntry), ct);
        await Other.InitializeMembershipTableAsync(false, ct);
        Equal(a, await SameHandles(ct));
        other = await Read(Other, ct);
        foreach (var tableAndSnapshot in new[] { (A, a), (Other, other) })
        {
            var (table, snapshot) = tableAndSnapshot;
            var key = otherEntry.SiloAddress;
            var point = ClusteringMembershipSnapshot.Capture(await table.ReadRowAsync(key, ct));
            Equal(snapshot with { Rows = snapshot.Rows.Clear().Add(key.ToParsableString(), snapshot.Row(key)) }, point);
        }

        // Row 2 exists only in A: a point-read implementation must not leak it into B.
        var absentFromOther = ClusteringMembershipSnapshot.Capture(await Other.ReadRowAsync(Entry(2).SiloAddress, ct));
        Equal(other with { Rows = other.Rows.Clear() }, absentFromOther);
    }, cancellationToken);

    /// <summary>G25: eligible Dead rows compact at the same version or in atomic +1 batches; retained canonical fields are preserved.</summary>
    public Task CleanupDefunctSiloEntries_RemovesOnlyStrictlyOldDeadRows(CancellationToken cancellationToken = default) => Run(async ct =>
    {
        var publishedHeartbeats = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var entry in CreateCleanupEntries(_seed)) await SeedCleanupEntry(entry);
        var other = await Insert(Other, Entry(1, SiloStatus.Dead), ct);
        var baseline = await SameHandles(ct);
        var history = new MembershipHistory();
        history.Observe(baseline);
        Check(baseline.Rows.Values.Count(row => IsEligibleForCleanup(row.Entry, T1, publishedHeartbeats)) == 1,
            "cleanup matrix must contain exactly one removable Dead row");
        await B.CleanupDefunctSiloEntriesAsync(new(T1), ct);
        var actual = await SameHandles(ct);
        AssertCleanup(baseline, actual, T1, publishedHeartbeats: publishedHeartbeats);
        history.Observe(actual);
        await A.CleanupDefunctSiloEntriesAsync(new(T1), ct);
        Equal(actual, await SameHandles(ct));
        var exclusiveCutoff = T1.AddTicks(1);
        Check(actual.Rows.Values.Count(row => IsEligibleForCleanup(row.Entry, exclusiveCutoff, publishedHeartbeats)) == 1,
            "the one-tick-later cutoff must select only the equality-boundary Dead row");
        await B.CleanupDefunctSiloEntriesAsync(new(exclusiveCutoff), ct);
        var boundaryRemoved = await SameHandles(ct);
        AssertCleanup(actual, boundaryRemoved, exclusiveCutoff, publishedHeartbeats: publishedHeartbeats);
        history.Observe(boundaryRemoved);
        for (var i = 200; i < 203; i++) await SeedCleanupEntry(Entry(i, SiloStatus.Dead));
        await CleanupWithConcurrentReads(publishedHeartbeats, ct);
        await SeedCleanupEntry(Entry(220, SiloStatus.Dead));
        await ConcurrentCleaners(publishedHeartbeats, ct);
        Equal(other, await Read(Other, ct));

        async Task SeedCleanupEntry(MembershipEntry entry)
        {
            var live = Copy(entry);
            if (live.Status == SiloStatus.Dead) live.Status = SiloStatus.Active;
            await Insert(A, live, ct);
            await Heartbeat(A, live, entry.IAmAliveTime, ct);
            publishedHeartbeats.Add(entry.SiloAddress.ToParsableString(), entry.IAmAliveTime);
            if (entry.Status == SiloStatus.Dead) await Update(A, entry, ct);
        }
    }, cancellationToken);

    /// <summary>G26: native verification confirms deletion before owner disposal, while the other cluster remains intact.</summary>
    public Task DeleteMembershipTableEntries_DeletesOwnClusterAndPreservesOtherCluster(CancellationToken cancellationToken = default) => Run(async ct =>
    {
        await Seed(ct);
        var other = await Insert(Other, Entry(1), ct);
        await _fixture.DeleteClusterAsync(A, _fixture.ClusterId, allowRetained: false, ct);
        await _fixture.AssertHistoryPresentAsync(_fixture.OtherClusterId, ct);
        Equal(other, await Read(Other, ct));
    }, cancellationToken);

    /// <summary>G27: deleting a different populated cluster never deletes the configured cluster.</summary>
    public Task DeleteMembershipTableEntries_DifferentClusterId_NeverDeletesConfiguredCluster(CancellationToken cancellationToken = default) => Run(async ct =>
    {
        await Seed(ct);
        var baseline = await SameHandles(ct);
        var otherBefore = await Insert(Other, Entry(1), ct);
        await _fixture.AssertHistoryPresentAsync(_fixture.ClusterId, ct);
        await _fixture.AssertHistoryPresentAsync(_fixture.OtherClusterId, ct);
        Equal(baseline, await SameHandles(ct));
        Equal(otherBefore, await Read(Other, ct));
        var deleted = await _fixture.DeleteClusterAsync(A, _fixture.OtherClusterId, allowRetained: true, ct);
        await _fixture.AssertHistoryPresentAsync(_fixture.ClusterId, ct);
        if (!deleted)
        {
            Equal(otherBefore, await Read(Other, ct));
            await _fixture.DeleteClusterAsync(Other, _fixture.OtherClusterId, allowRetained: false, ct);
            await _fixture.AssertHistoryPresentAsync(_fixture.ClusterId, ct);
        }
        Equal(baseline, await SameHandles(ct));

    }, cancellationToken);

    private MembershipEntry Entry(int index, SiloStatus status = SiloStatus.Created) => CreateEntry(index, _seed, status);

    private async Task Run(Func<CancellationToken, Task> action, CancellationToken cancellationToken, [CallerMemberName] string guarantee = "")
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try { await action(timeout.Token).WaitAsync(timeout.Token); }
        catch (ClusteringConformanceException exception)
        {
            var failure = ClusteringTestKitDiagnostics.CreateFailure(_fixture.ProviderName, guarantee, _fixture.ClusterId, "A1/A2/B1", _seed, exception.Message, exception);
            _output?.Invoke(failure.Message);
            throw failure;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw ClusteringTestKitDiagnostics.CreateFailure(_fixture.ProviderName, guarantee, _fixture.ClusterId, "A1/A2/B1", _seed,
                "bounded scenario timed out; required operation/readiness/completion evidence is missing");
        }
    }

    private static void Check(bool condition, string detail) => ClusteringTestKitDiagnostics.Require(condition, detail);
    internal static void Equal(ClusteringMembershipSnapshot expected, ClusteringMembershipSnapshot observed, bool complete = false)
    {
        var difference = complete ? expected.CompareComplete(observed) : expected.CompareCanonical(observed);
        Check(difference is null, difference ?? "equal");
    }
    internal static void EqualRow(MembershipEntrySnapshot expected, MembershipEntrySnapshot observed, bool complete = false)
        => Check(expected.Difference(observed, complete) is null, expected.Difference(observed, complete) ?? "equal");
    internal static async Task<ClusteringMembershipSnapshot> Read(IMembershipTable table, CancellationToken ct)
        => ClusteringMembershipSnapshot.Capture(await table.ReadAllAsync(ct));

    private async Task Seed(CancellationToken ct)
    {
        await Insert(A, Entry(1), ct);
        await Insert(B, Entry(2), ct);
    }

    private async Task<ClusteringMembershipSnapshot> SameHandles(CancellationToken ct)
    {
        var first = await Read(A, ct);
        Equal(first, await Read(B, ct));
        return first;
    }

    internal static async Task<ClusteringMembershipSnapshot> Insert(IMembershipTable table, MembershipEntry input, CancellationToken ct)
    {
        var before = await Read(table, ct);
        var detached = Copy(input);
        Check(await table.InsertRowAsync(input, before.Next(), ct), $"current-table insert returned false; table={before.Next()}, identity={input.SiloAddress}");
        var after = await Read(table, ct);
        AssertCommit(before, after, detached);
        return after;
    }

    internal static async Task<ClusteringMembershipSnapshot> Update(IMembershipTable table, MembershipEntry input, CancellationToken ct)
    {
        var before = await Read(table, ct);
        var detached = Copy(input);
        var rowEtag = before.Row(input.SiloAddress).Etag;
        Check(await table.UpdateRowAsync(input, rowEtag, before.Next(), ct),
            $"fresh-token update returned false; table={before.Next()}, row ETag={rowEtag}, identity={input.SiloAddress}");
        var after = await Read(table, ct);
        AssertCommit(before, after, detached);
        return after;
    }

    internal static void AssertCommit(ClusteringMembershipSnapshot before, ClusteringMembershipSnapshot after, MembershipEntry input)
    {
        var id = input.SiloAddress.ToParsableString();
        Check(after.Version == before.Version + 1, $"commit integer: expected={before.Version + 1}, observed={after.Version}");
        Check(!string.IsNullOrEmpty(after.TableEtag) && after.TableEtag != before.TableEtag,
            $"commit table ETag must change: old={before.TableEtag}, observed={after.TableEtag}");
        Check(after.Rows.TryGetValue(id, out var row), $"committed identity missing={id}");
        var expectedEntry = MembershipEntrySnapshot.Capture(input);
        Equal(before with
        {
            Version = before.Version + 1,
            TableEtag = after.TableEtag,
            Rows = before.Rows.SetItem(id, new(expectedEntry, row!.Etag))
        }, after);
    }

    private static bool IsEligibleForCleanup(
        MembershipEntrySnapshot entry, DateTime cutoff, IReadOnlyDictionary<string, DateTime>? publishedHeartbeats = null)
    {
        // Controlled cleanup histories use owner publications; raw heartbeat observations may lag.
        var expected = publishedHeartbeats is null ? entry : entry with { IAmAliveTime = publishedHeartbeats[entry.Identity] };
        return expected.Status == SiloStatus.Dead && expected.GetEffectiveUpdateTime() < cutoff;
    }

    internal static void AssertCleanup(
        ClusteringMembershipSnapshot before,
        ClusteringMembershipSnapshot after,
        DateTime cutoff,
        bool requireAllEligible = true,
        IReadOnlyDictionary<string, DateTime>? publishedHeartbeats = null)
    {
        var eligible = before.Rows.Where(pair => IsEligibleForCleanup(pair.Value.Entry, cutoff, publishedHeartbeats))
            .Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        var removed = before.Rows.Keys.Except(after.Rows.Keys).ToArray();
        Check(removed.All(eligible.Contains), "cleanup removed an ineligible identity");
        Check(after.Rows.Keys.All(before.Rows.ContainsKey), "cleanup added an identity");
        if (requireAllEligible)
            Check(eligible.SetEquals(removed), "cleanup left eligible Dead rows in the table");
        if (removed.Length == 0)
        {
            Equal(before, after);
            return;
        }

        var delta = (long)after.Version - before.Version;
        Check(delta >= 0 && delta <= removed.Length,
            $"cleanup version: expected delta in [0,{removed.Length}], observed={delta}");
        Check(!string.IsNullOrEmpty(after.TableEtag)
            && (delta == 0 ? after.TableEtag == before.TableEtag : after.TableEtag != before.TableEtag),
            $"cleanup table ETag must match version progress: delta={delta}, old={before.TableEtag}, observed={after.TableEtag}");
        var expected = before with
        {
            Version = after.Version,
            TableEtag = after.TableEtag,
            Rows = before.Rows.RemoveRange(removed)
        };
        Equal(expected, after);
    }

    private async Task CleanupWithConcurrentReads(IReadOnlyDictionary<string, DateTime> publishedHeartbeats, CancellationToken ct)
    {
        var before = await SameHandles(ct);
        Check(before.Rows.Values.Count(row => IsEligibleForCleanup(row.Entry, T1, publishedHeartbeats)) == 3,
            "batched cleanup requires exactly three eligible Dead rows");
        var start = Gate();
        var ready = Enumerable.Range(0, 3).Select(_ => Gate()).ToArray();
        var observations = new System.Collections.Concurrent.ConcurrentQueue<ClusteringMembershipSnapshot>();
        async Task Cleanup()
        {
            ready[0].SetResult();
            await start.Task.WaitAsync(ct);
            await B.CleanupDefunctSiloEntriesAsync(new(T1), ct);
        }

        async Task Reader(int index)
        {
            ready[index].SetResult();
            await start.Task.WaitAsync(ct);
            var previous = before;
            for (var i = 0; i < 8; i++)
            {
                var sample = await Read(index == 1 ? A : B, ct);
                AssertCleanup(previous, sample, T1, requireAllEligible: false, publishedHeartbeats: publishedHeartbeats);
                observations.Enqueue(sample);
                previous = sample;
            }
        }

        var workers = new[] { Cleanup(), Reader(1), Reader(2) };
        await Task.WhenAll(ready.Select(gate => gate.Task)).WaitAsync(ct);
        start.SetResult();
        await Task.WhenAll(workers).WaitAsync(ct);
        var after = await SameHandles(ct);
        AssertCleanup(before, after, T1, publishedHeartbeats: publishedHeartbeats);
        observations.Enqueue(before);
        observations.Enqueue(after);
        var history = new MembershipHistory();
        var previous = before;
        foreach (var observed in observations.OrderBy(snapshot => snapshot.Version).ThenByDescending(snapshot => snapshot.Rows.Count))
        {
            AssertCleanup(previous, observed, T1, requireAllEligible: false, publishedHeartbeats: publishedHeartbeats);
            history.Observe(observed);
            previous = observed;
        }

        await A.CleanupDefunctSiloEntriesAsync(new(T1), ct);
        Equal(after, await SameHandles(ct));
    }

    private async Task ConcurrentCleaners(IReadOnlyDictionary<string, DateTime> publishedHeartbeats, CancellationToken ct)
    {
        var before = await SameHandles(ct);
        Check(before.Rows.Values.Count(row => IsEligibleForCleanup(row.Entry, T1, publishedHeartbeats)) == 1,
            "the two-cleaner race must have one eligible Dead row");
        var start = Gate();
        var ready = new[] { Gate(), Gate() };
        async Task Clean(IMembershipTable table, int index)
        {
            ready[index].SetResult();
            await start.Task.WaitAsync(ct);
            await table.CleanupDefunctSiloEntriesAsync(new(T1), ct);
        }

        var cleaners = new[] { Clean(A, 0), Clean(B, 1) };
        await Task.WhenAll(ready.Select(gate => gate.Task)).WaitAsync(ct);
        start.SetResult();
        await Task.WhenAll(cleaners).WaitAsync(ct);
        AssertCleanup(before, await SameHandles(ct), T1, publishedHeartbeats: publishedHeartbeats);
    }

    internal static async Task Heartbeat(IMembershipTable table, MembershipEntry entry, DateTime time, CancellationToken ct)
    {
        var before = await Read(table, ct);
        var payload = new MembershipEntry { SiloAddress = entry.SiloAddress, IAmAliveTime = time };
        await table.UpdateIAmAliveAsync(payload, ct);
        var after = await Read(table, ct);
        AssertHeartbeat(before, after);
    }

    internal static void AssertHeartbeat(ClusteringMembershipSnapshot before, ClusteringMembershipSnapshot after)
        => Equal(before, after);

    private async Task Reject(Func<Task<bool>> write, CancellationToken ct)
    {
        var before = await SameHandles(ct);
        Check(!await write(), "conditional write unexpectedly succeeded");
        Equal(before, await SameHandles(ct));
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal async Task SeedConcurrentRows(CancellationToken ct)
    {
        var current = ClusteringMembershipSnapshot.Capture(await A.ReadRowAsync(Entry(1).SiloAddress, ct));
        Check(current.Rows.Count == 0, "concurrent-read setup requires an unused fixture identity");
        var expectedRows = current.Rows.ToBuilder();
        for (var i = 1; i <= _concurrencyRowCount; i++)
        {
            var input = Entry(i);
            var expectedEntry = MembershipEntrySnapshot.Capture(input);
            var writer = i % 2 == 0 ? B : A;
            var reader = i % 2 == 0 ? A : B;
            Check(await writer.InsertRowAsync(input, current.Next(), ct),
                $"concurrent-read setup insert failed: index={i}, table={current.Next()}");
            var observed = ClusteringMembershipSnapshot.Capture(await reader.ReadRowAsync(input.SiloAddress, ct));
            AssertCommit(current with { Rows = current.Rows.Clear() }, observed, expectedEntry.ToEntry());
            expectedRows.Add(expectedEntry.Identity, new(expectedEntry, observed.Row(input.SiloAddress).Etag));
            current = observed;
        }
        Equal(current with { Rows = expectedRows.ToImmutable() }, await SameHandles(ct));
    }

    private async Task ConcurrentReads(bool pointReads, CancellationToken ct)
    {
        await SeedConcurrentRows(ct);
        for (var round = 0; round < 6; round++)
        {
            var before = await SameHandles(ct);
            var cleanup = round >= 4;
            var target = before.Row(Entry(1 + round % 2).SiloAddress).Entry.ToEntry();
            if (cleanup)
            {
                target.Status = SiloStatus.Dead;
                await Update(A, target, ct);
                before = await SameHandles(ct);
            }
            else
            {
                target = Forward(target);
            }
            var start = Gate();
            var ready = Enumerable.Range(0, 4).Select(_ => Gate()).ToArray();
            var observations = new System.Collections.Concurrent.ConcurrentQueue<(ClusteringMembershipSnapshot Snapshot, SiloAddress Key)>();
            async Task Writer()
            {
                ready[0].SetResult();
                await start.Task.WaitAsync(ct);
                if (cleanup)
                    await B.CleanupDefunctSiloEntriesAsync(new(T1), ct);
                else
                    Check(await B.UpdateRowAsync(target, before.Row(target.SiloAddress).Etag, before.Next(), ct), "controlled atomic-read writer failed");
            }

            async Task Reader(int index)
            {
                ready[index].SetResult();
                await start.Task.WaitAsync(ct);
                var key = index switch { 1 => target.SiloAddress, 2 => Entry(3).SiloAddress, _ => Entry(_concurrencyRowCount + 1).SiloAddress };
                var table = index % 2 == 0 ? B : A;
                var sample = ClusteringMembershipSnapshot.Capture(pointReads ? await table.ReadRowAsync(key, ct) : await table.ReadAllAsync(ct));
                var expectedBefore = pointReads ? before.Select(key) : before;
                var id = target.SiloAddress.ToParsableString();
                if (sample.Version == before.Version)
                {
                    if (cleanup && !sample.Rows.ContainsKey(id))
                        Equal(expectedBefore with { Rows = expectedBefore.Rows.Remove(id) }, sample);
                    else
                        Equal(expectedBefore, sample);
                }
                else
                {
                    Check(sample.Version == before.Version + 1 && sample.TableEtag != before.TableEtag && !string.IsNullOrEmpty(sample.TableEtag),
                        $"round={round}, reader={index}: invalid atomic version/token pair, expected={before.Version} or {before.Version + 1}, observed={sample.Version}/{sample.TableEtag}");
                    var expectedAfter = before with
                    {
                        Version = before.Version + 1,
                        TableEtag = sample.TableEtag,
                        Rows = cleanup ? before.Rows.Remove(id)
                            : before.Rows.SetItem(id, new(MembershipEntrySnapshot.Capture(target), before.Rows[id].Etag))
                    };
                    if (cleanup)
                    {
                        Check(!sample.Rows.ContainsKey(id), "atomic cleanup still returned the removed identity");
                    }
                    Equal(pointReads ? expectedAfter.Select(key) : expectedAfter, sample);
                }
                observations.Enqueue((sample, key));
            }

            var workers = new[] { Writer(), Reader(1), Reader(2), Reader(3) };
            await Task.WhenAll(ready.Select(g => g.Task)).WaitAsync(ct);
            start.SetResult();
            await Task.WhenAll(workers).WaitAsync(ct);
            var committed = await SameHandles(ct);
            if (cleanup) AssertCleanup(before, committed, T1);
            else AssertCommit(before, committed, target);
            // Once the writer completes, pin down the committed table token as well as its canonical fields.
            foreach (var (sample, key) in observations)
            {
                var expectedBefore = pointReads ? before.Select(key) : before;
                var expectedAfter = pointReads ? committed.Select(key) : committed;
                Check(expectedBefore.CompareCanonical(sample) is null || expectedAfter.CompareCanonical(sample) is null,
                    $"atomic read matches neither committed view: before={expectedBefore.CompareCanonical(sample)}; after={expectedAfter.CompareCanonical(sample)}");
            }
        }
    }
}
