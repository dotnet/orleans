using Orleans.Runtime;

namespace Orleans.Clustering.TestKit.Tests;

internal enum MembershipFault
{
    IgnoreTableToken, FalseWriteChangesMembership, VersionJump, HeartbeatInvalidatesRowCondition, HeartbeatChangesTableToken, HeartbeatChangesMembership,
    AliasInsert, AliasUpdate, AliasRead, MutateRetainedReads, ClearOnInitialize, InsertChangesExistingRow,
    CleanupNonDead, CleanupCutoffInclusive, DeleteConfiguredScope, TornReadAll, TornReadRow, RefuseStatusWrite,
    IgnoreUpdatedVoteTime, PreserveClearedVotes, CrossClusterPointRead,
    ResurrectCompactedRow, DeletePrefixScopes, HeartbeatStorageFailure,
    CleanupChangesRetainedFields, CleanupVersionRollback, CleanupRoundsExclusiveCutoff, TornCleanupReadAll, TornCleanupReadRow,
    DeleteNoOp, DeletePartial, DeleteStorageFailure, DeleteCommitThenFailure, DeleteThenRejectForeign, HeartbeatCancellation
}

internal sealed class MembershipFaultController(MembershipFault fault)
{
    internal MembershipFault Fault { get; } = fault;
    internal IdealizedMembershipBackend Backend { get; init; } = new();
    internal int Injected;
    internal int UpdateCalls;
    internal readonly Dictionary<string, List<MembershipEntry>> Retained = new(StringComparer.Ordinal);
    internal readonly TaskCompletionSource ReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource Committed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource WriterArmed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal MembershipTableData? TornBefore;
    internal int TornArmed;
    internal int ReadsAtFiveRows;
    internal bool CleanupCompleted;
    internal int ReadsWithDeadTarget;
    internal readonly InvalidOperationException HeartbeatFailure = new("heartbeat-backend-failure");
    internal OperationCanceledException HeartbeatCancellation { get; init; } = new("heartbeat-backend-cancellation");
    internal readonly InvalidOperationException DeletionFailure = new("deletion-backend-failure");

    internal MembershipTableTestFixture Fixture()
        => new("Deliberate-" + Fault, (_, cluster, _) =>
            ValueTask.FromResult(new MembershipTableTestHandle(new FaultyMembershipTable(this, cluster, Backend.Create(cluster)),
                () => Backend.DisposeHandleAsync(cluster))), Backend.IsDeletedAsync);
}

internal sealed class FaultyMembershipTable(MembershipFaultController control, string cluster, IMembershipTable inner) : IMembershipTable
{
    private MembershipFault Fault => control.Fault;

    public async Task InitializeMembershipTableAsync(bool tryInitTableVersion, CancellationToken cancellationToken = default)
    {
        await inner.InitializeMembershipTableAsync(tryInitTableVersion, cancellationToken);
        if (Fault == MembershipFault.ClearOnInitialize)
            Mutate(p => { if (p.Rows.Count > 0) { p.Rows.Clear(); control.Injected++; } });
    }

    public Task DeleteMembershipTableEntriesAsync(string clusterId, CancellationToken cancellationToken = default)
    {
        if (Fault == MembershipFault.DeleteStorageFailure) throw control.DeletionFailure;
        if (Fault == MembershipFault.DeleteNoOp)
        {
            control.Injected++;
            return Task.CompletedTask;
        }
        if (Fault == MembershipFault.DeletePartial)
        {
            lock (control.Backend.Sync)
            {
                if (control.Backend.Partitions.TryGetValue(clusterId, out var partition) && partition.Rows.Count > 1)
                    partition.Rows.Remove(partition.Rows.Keys.First());
                control.Injected++;
            }
            return Task.CompletedTask;
        }
        if (Fault is MembershipFault.DeleteCommitThenFailure or MembershipFault.DeleteThenRejectForeign)
            return DeleteThenFail();
        if (Fault == MembershipFault.DeletePrefixScopes)
        {
            lock (control.Backend.Sync)
            {
                foreach (var matching in control.Backend.Partitions.Keys.Where(id => id.StartsWith(clusterId, StringComparison.Ordinal)).ToArray())
                {
                    control.Backend.Partitions.Remove(matching);
                    control.Injected++;
                }
            }
            return Task.CompletedTask;
        }
        if (Fault == MembershipFault.DeleteConfiguredScope && clusterId != cluster)
        {
            control.Injected++;
            return inner.DeleteMembershipTableEntriesAsync(cluster, cancellationToken);
        }
        return inner.DeleteMembershipTableEntriesAsync(clusterId, cancellationToken);

        async Task DeleteThenFail()
        {
            await inner.DeleteMembershipTableEntriesAsync(clusterId, cancellationToken);
            control.Injected++;
            if (Fault == MembershipFault.DeleteThenRejectForeign && clusterId != cluster)
                throw new ArgumentException("Scope rejection after destructive side effect.", nameof(clusterId));
            if (Fault == MembershipFault.DeleteCommitThenFailure) throw control.DeletionFailure;
        }
    }

    public async Task CleanupDefunctSiloEntriesAsync(DateTimeOffset beforeDate, CancellationToken cancellationToken = default)
    {
        if (Fault is MembershipFault.TornCleanupReadAll or MembershipFault.TornCleanupReadRow)
        {
            control.TornBefore = await inner.ReadAllAsync(cancellationToken);
            Volatile.Write(ref control.TornArmed, 1);
            control.WriterArmed.TrySetResult();
            await control.ReadStarted.Task.WaitAsync(cancellationToken);
        }
        if (Fault == MembershipFault.CleanupRoundsExclusiveCutoff && beforeDate.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            beforeDate = new DateTimeOffset(beforeDate.Ticks - beforeDate.Ticks % TimeSpan.TicksPerSecond, beforeDate.Offset);
            control.Injected++;
        }
        var before = Fault == MembershipFault.CleanupVersionRollback ? await inner.ReadAllAsync(cancellationToken) : null;
        await inner.CleanupDefunctSiloEntriesAsync(beforeDate, cancellationToken);
        control.CleanupCompleted = true;
        if (Fault is MembershipFault.TornCleanupReadAll or MembershipFault.TornCleanupReadRow)
            control.Committed.TrySetResult();
        if (before is not null)
            Mutate(partition =>
            {
                if (partition.Rows.Count != before.Members.Count)
                {
                    partition.Version = before.Version.Version - 1;
                    partition.Etag = before.Version.VersionEtag;
                    control.Injected++;
                }
            });
        if (Fault == MembershipFault.CleanupChangesRetainedFields)
            Mutate(partition =>
            {
                foreach (var row in partition.Rows.Values) row.Item1.HostName += "-cleanup-drift";
                control.Injected++;
            });
        if (Fault is MembershipFault.CleanupNonDead or MembershipFault.CleanupCutoffInclusive)
            Mutate(p =>
            {
                foreach (var row in p.Rows.Where(row =>
                {
                    var e = row.Value.Item1;
                    var time = (e.SuspectTimes ?? []).Select(v => v.Item2).Append(e.StartTime).Append(e.IAmAliveTime).Max();
                    return Fault == MembershipFault.CleanupNonDead ? time < beforeDate.UtcDateTime
                        : e.Status == SiloStatus.Dead && time == beforeDate.UtcDateTime;
                }).ToArray())
                {
                    p.Rows.Remove(row.Key);
                    control.Injected++;
                }
            });
    }

    public async Task<bool> InsertRowAsync(MembershipEntry entry, TableVersion tableVersion, CancellationToken cancellationToken = default)
    {
        if (Fault == MembershipFault.IgnoreTableToken)
        {
            // Make the deliberate violation deterministic: serialize both "ignore table token" commits.
            lock (control.Backend.Sync)
            {
                var current = inner.ReadAllAsync(cancellationToken).GetAwaiter().GetResult().Version.Next();
                if (current.VersionEtag != tableVersion.VersionEtag) control.Injected++;
                return inner.InsertRowAsync(entry, current, cancellationToken).GetAwaiter().GetResult();
            }
        }
        if (Fault == MembershipFault.VersionJump)
        {
            tableVersion = new(tableVersion.Version + 1, tableVersion.VersionEtag);
            control.Injected++;
        }
        var success = await inner.InsertRowAsync(entry, tableVersion, cancellationToken);
        if (success && Fault == MembershipFault.InsertChangesExistingRow)
            Mutate(p =>
            {
                foreach (var other in p.Rows.Where(pair => !pair.Key.Equals(entry.SiloAddress)))
                {
                    other.Value.Item1.HostName = "unexpected-seed-mutation";
                    control.Injected++;
                }
            });
        if (success && Fault == MembershipFault.AliasInsert)
            Mutate(p => { p.Rows[entry.SiloAddress] = Tuple.Create(entry, p.Rows[entry.SiloAddress].Item2); control.Injected++; });
        return success;
    }

    public async Task<bool> UpdateRowAsync(MembershipEntry entry, string etag, TableVersion tableVersion, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref control.UpdateCalls);
        if (Fault == MembershipFault.ResurrectCompactedRow && control.CleanupCompleted
            && (await inner.ReadRowAsync(entry.SiloAddress, cancellationToken)).Members.Count == 0)
        {
            control.Injected++;
            return await inner.InsertRowAsync(entry, tableVersion, cancellationToken);
        }
        if (Fault is MembershipFault.IgnoreUpdatedVoteTime or MembershipFault.PreserveClearedVotes)
        {
            var existing = (await inner.ReadRowAsync(entry.SiloAddress, cancellationToken)).TryGet(entry.SiloAddress);
            if (existing is not null)
            {
                entry = IdealizedMembershipTable.Clone(entry);
                if (Fault == MembershipFault.IgnoreUpdatedVoteTime && entry.SuspectTimes is not null)
                {
                    for (var i = 0; i < entry.SuspectTimes.Count; i++)
                    {
                        var vote = entry.SuspectTimes[i];
                        var previous = existing.Item1.SuspectTimes?.FirstOrDefault(p => p.Item1.Equals(vote.Item1));
                        if (previous is not null && previous.Item2 != vote.Item2)
                        {
                            entry.SuspectTimes[i] = previous;
                            control.Injected++;
                        }
                    }
                }
                if (Fault == MembershipFault.PreserveClearedVotes && entry.SuspectTimes is not { Count: > 0 }
                    && existing.Item1.SuspectTimes is { Count: > 0 })
                {
                    entry.SuspectTimes = existing.Item1.SuspectTimes;
                    control.Injected++;
                }
            }
        }
        if (Fault == MembershipFault.RefuseStatusWrite) { control.Injected++; return false; }
        if (Fault == MembershipFault.IgnoreTableToken)
        {
            lock (control.Backend.Sync)
            {
                var current = inner.ReadAllAsync(cancellationToken).GetAwaiter().GetResult().Version.Next();
                if (current.VersionEtag != tableVersion.VersionEtag) control.Injected++;
                return inner.UpdateRowAsync(entry, etag, current, cancellationToken).GetAwaiter().GetResult();
            }
        }
        if (Fault is MembershipFault.TornReadAll or MembershipFault.TornReadRow)
        {
            control.TornBefore = await inner.ReadAllAsync(cancellationToken);
            Volatile.Write(ref control.TornArmed, 1);
            control.WriterArmed.TrySetResult();
            await control.ReadStarted.Task.WaitAsync(cancellationToken);
        }
        var success = await inner.UpdateRowAsync(entry, etag, tableVersion, cancellationToken);
        if (success && Fault == MembershipFault.AliasUpdate)
            Mutate(p => { p.Rows[entry.SiloAddress] = Tuple.Create(entry, p.Rows[entry.SiloAddress].Item2); control.Injected++; });
        if (!success && Fault == MembershipFault.FalseWriteChangesMembership)
            Mutate(p =>
            {
                if (p.Rows.TryGetValue(entry.SiloAddress, out var row))
                {
                    row.Item1.HostName += "-failed-write";
                    control.Injected++;
                }
            });
        if (success && Fault == MembershipFault.MutateRetainedReads)
            MutateRetained(entry);
        if (Fault is MembershipFault.TornReadAll or MembershipFault.TornReadRow)
            control.Committed.TrySetResult();
        return success;
    }

    public async Task UpdateIAmAliveAsync(MembershipEntry entry, CancellationToken cancellationToken = default)
    {
        if (Fault == MembershipFault.HeartbeatStorageFailure) throw control.HeartbeatFailure;
        if (Fault == MembershipFault.HeartbeatCancellation) throw control.HeartbeatCancellation;
        await inner.UpdateIAmAliveAsync(entry, cancellationToken);
        if (Fault == MembershipFault.HeartbeatInvalidatesRowCondition)
            Mutate(p =>
            {
                p.Rows[entry.SiloAddress] = Tuple.Create(p.Rows[entry.SiloAddress].Item1, control.Backend.Token());
                control.Injected++;
            });
        if (Fault == MembershipFault.HeartbeatChangesTableToken)
            Mutate(p => { p.Etag = control.Backend.Token(); control.Injected++; });
        if (Fault == MembershipFault.HeartbeatChangesMembership)
            Mutate(p => { p.Rows[entry.SiloAddress].Item1.ProxyPort++; control.Injected++; });
        if (Fault == MembershipFault.MutateRetainedReads) MutateRetained(entry);
    }

    public async Task<MembershipTableData> ReadAllAsync(CancellationToken cancellationToken = default)
        => await Read(null, cancellationToken);
    public async Task<MembershipTableData> ReadRowAsync(SiloAddress key, CancellationToken cancellationToken = default)
    {
        if (Fault == MembershipFault.CrossClusterPointRead)
        {
            string? wrongCluster;
            lock (control.Backend.Sync)
            {
                wrongCluster = control.Backend.Partitions.FirstOrDefault(p => p.Key != cluster && p.Value.Rows.ContainsKey(key)).Key;
            }
            if (wrongCluster is not null)
            {
                control.Injected++;
                await using var handle = new MembershipTableTestHandle(control.Backend.Create(wrongCluster),
                    () => control.Backend.DisposeHandleAsync(wrongCluster));
                return await handle.Table.ReadRowAsync(key, cancellationToken);
            }
        }
        return await Read(key, cancellationToken);
    }

    private async Task<MembershipTableData> Read(SiloAddress? key, CancellationToken ct)
    {
        if (Fault is MembershipFault.TornCleanupReadAll or MembershipFault.TornCleanupReadRow)
        {
            var current = await inner.ReadAllAsync(ct);
            // The Dead transition has one verification read and two baseline reads before
            // the gated race. Hold subsequent readers until cleanup has captured that view.
            var racingRead = Volatile.Read(ref control.TornArmed) != 0
                || (current.Members.Any(row => row.Item1.Status == SiloStatus.Dead)
                    && Interlocked.Increment(ref control.ReadsWithDeadTarget) > 3);
            if (racingRead && ((key is null && Fault == MembershipFault.TornCleanupReadAll)
                || (key is not null && Fault == MembershipFault.TornCleanupReadRow)))
            {
                await control.WriterArmed.Task.WaitAsync(ct);
                control.ReadStarted.TrySetResult();
                await control.Committed.Task.WaitAsync(ct);
                current = await inner.ReadAllAsync(ct);
                control.Injected++;
                return new(control.TornBefore!.Members.Where(row => key is null || row.Item1.SiloAddress.Equals(key)).ToList(), current.Version);
            }
        }
        var torn = (key is null && Fault == MembershipFault.TornReadAll) || (key is not null && Fault == MembershipFault.TornReadRow);
        if (torn)
        {
            // Two setup validation reads and two round-boundary reads precede the gated race.
            var current = await inner.ReadAllAsync(ct);
            var fullReads = key is null && current.Version.Version == 5
                ? Interlocked.Increment(ref control.ReadsAtFiveRows)
                : Volatile.Read(ref control.ReadsAtFiveRows);
            if (fullReads > 4 || (key is not null && fullReads >= 4))
                await control.WriterArmed.Task.WaitAsync(ct);
            if (Volatile.Read(ref control.TornArmed) != 0)
            {
                control.ReadStarted.TrySetResult();
                await control.Committed.Task.WaitAsync(ct);
                current = await inner.ReadAllAsync(ct);
                control.Injected++;
                var oldRows = control.TornBefore!.Members.Where(p => key is null || p.Item1.SiloAddress.Equals(key)).ToList();
                return new(oldRows, current.Version);
            }
        }
        var result = key is null ? await inner.ReadAllAsync(ct) : await inner.ReadRowAsync(key, ct);
        if (Fault == MembershipFault.AliasRead)
        {
            lock (control.Backend.Sync)
            {
                var partition = control.Backend.Partitions[cluster];
                result = new(partition.Rows.Where(p => key is null || p.Key.Equals(key)).Select(p => p.Value).ToList(), result.Version);
                control.Injected++;
            }
        }
        if (Fault == MembershipFault.MutateRetainedReads)
        {
            lock (control.Backend.Sync)
            {
                foreach (var row in result.Members)
                {
                    var id = cluster + "/" + row.Item1.SiloAddress.ToParsableString();
                    if (!control.Retained.TryGetValue(id, out var entries)) control.Retained[id] = entries = [];
                    entries.Add(row.Item1);
                }
            }
        }
        return result;
    }

    private void MutateRetained(MembershipEntry input)
    {
        lock (control.Backend.Sync)
        {
            if (control.Retained.TryGetValue(cluster + "/" + input.SiloAddress.ToParsableString(), out var retained))
                foreach (var entry in retained)
                {
                    entry.IAmAliveTime = input.IAmAliveTime;
                    entry.HostName = input.HostName;
                    entry.Status = input.Status;
                    entry.SuspectTimes?.Clear();
                    control.Injected++;
                }
        }
    }

    private void Mutate(Action<IdealizedMembershipBackend.Partition> action)
    {
        lock (control.Backend.Sync)
            if (control.Backend.Partitions.TryGetValue(cluster, out var partition)) action(partition);
    }

    public Task InitializeMembershipTable(bool tryInitTableVersion) => InitializeMembershipTableAsync(tryInitTableVersion);
    public Task DeleteMembershipTableEntries(string clusterId) => DeleteMembershipTableEntriesAsync(clusterId);
    public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate) => CleanupDefunctSiloEntriesAsync(beforeDate);
    public Task<MembershipTableData> ReadAll() => ReadAllAsync();
    public Task<MembershipTableData> ReadRow(SiloAddress key) => ReadRowAsync(key);
    public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion) => InsertRowAsync(entry, tableVersion);
    public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion) => UpdateRowAsync(entry, etag, tableVersion);
    public Task UpdateIAmAlive(MembershipEntry entry) => UpdateIAmAliveAsync(entry);
}
