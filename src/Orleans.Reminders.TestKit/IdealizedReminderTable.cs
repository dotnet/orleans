using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Reminders.TestKit;

/// <summary>
/// A deterministic, strongly consistent, in-memory reference implementation of the <see cref="IReminderTable"/>
/// contract, written independently of every production reminder provider.
/// </summary>
/// <remarks>
/// <para>
/// The oracle exists so that the conformance suite can be validated against a known-correct implementation before it
/// is pointed at a real provider. It is not a wrapper over a production provider: it maintains its own storage, its
/// own monotonic ETag sequence and its own range evaluation derived directly from the documented contract.
/// </para>
/// <para>Introspection: <see cref="Snapshot"/>, <see cref="Operations"/>, <see cref="OperationCount"/>.</para>
/// <para>
/// Controls: <see cref="SetAvailable"/> (storage outage), <see cref="InjectFailure"/> (transient failures),
/// <see cref="BlockNext"/> (deterministic synchronization barriers), <see cref="FreezeReads()"/> (stale snapshots) and
/// cancellation observation on <see cref="StartAsync"/> and <see cref="StopAsync"/>.
/// </para>
/// <para>
/// Invariant violations are detected at the operation which causes them and are reported as
/// <see cref="ReminderConformanceException"/> carrying a structured <see cref="ReminderFailureReport"/>.
/// </para>
/// </remarks>
public sealed class IdealizedReminderTable : IReminderTable
{
    private readonly object _gate = new();
    private readonly Dictionary<(GrainId GrainId, string ReminderName), ReminderTableRecord> _records = new();
    private readonly HashSet<string> _issuedETags = new(StringComparer.Ordinal);
    private readonly List<ReminderTableOperation> _operations = [];
    private readonly List<ReminderTableOperationGate> _gates = [];
    private readonly Dictionary<ReminderTableOperationKind, Queue<Func<Exception>>> _injectedFailures = [];

    private Dictionary<(GrainId GrainId, string ReminderName), ReminderTableRecord>? _frozenReads;
    private HashSet<ReminderTableOperationKind>? _frozenReadKinds;
    private long _etagCounter;
    private long _sequence;
    private long _versionCounter;
    private bool _available = true;
    private bool _started;

    /// <summary>
    /// Initializes a new instance of the <see cref="IdealizedReminderTable"/> class.
    /// </summary>
    /// <param name="name">A name used to identify this instance in diagnostics.</param>
    public IdealizedReminderTable(string name = "IdealizedReminderTable")
    {
        Name = string.IsNullOrWhiteSpace(name) ? "IdealizedReminderTable" : name;
    }

    /// <summary>
    /// Gets the name used to identify this instance in diagnostics.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets a value indicating whether <see cref="StartAsync"/> has completed more recently than <see cref="StopAsync"/>.
    /// </summary>
    public bool IsStarted
    {
        get
        {
            lock (_gate)
            {
                return _started;
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the table is currently serving requests.
    /// </summary>
    public bool IsAvailable
    {
        get
        {
            lock (_gate)
            {
                return _available;
            }
        }
    }

    /// <summary>
    /// Returns a deterministically ordered snapshot of every durable record.
    /// </summary>
    /// <returns>The records ordered by uniform hash code, grain identifier and reminder name.</returns>
    public IReadOnlyList<ReminderTableRecord> Snapshot()
    {
        lock (_gate)
        {
            return OrderRecords(_records.Values).ToList();
        }
    }

    /// <summary>
    /// Attempts to read a single durable record.
    /// </summary>
    /// <param name="grainId">The grain identifier.</param>
    /// <param name="reminderName">The reminder name.</param>
    /// <returns>The record, or <see langword="null"/> when it does not exist.</returns>
    public ReminderTableRecord? Find(GrainId grainId, string reminderName)
    {
        lock (_gate)
        {
            return _records.TryGetValue((grainId, reminderName), out var record) ? record : null;
        }
    }

    /// <summary>
    /// Returns the ordered log of every operation observed by this table.
    /// </summary>
    public IReadOnlyList<ReminderTableOperation> Operations
    {
        get
        {
            lock (_gate)
            {
                return _operations.ToList();
            }
        }
    }

    /// <summary>
    /// Counts the operations of the specified kind observed so far.
    /// </summary>
    /// <param name="kind">The operation kind.</param>
    /// <returns>The count.</returns>
    public int OperationCount(ReminderTableOperationKind kind)
    {
        lock (_gate)
        {
            return _operations.Count(operation => operation.Kind == kind);
        }
    }

    /// <summary>
    /// Clears the recorded operation log without affecting durable state.
    /// </summary>
    public void ClearOperations()
    {
        lock (_gate)
        {
            _operations.Clear();
        }
    }

    /// <summary>
    /// Marks the table available or unavailable, simulating a storage outage and its recovery.
    /// </summary>
    /// <param name="available">Whether the table should serve requests.</param>
    public void SetAvailable(bool available)
    {
        lock (_gate)
        {
            _available = available;
        }
    }

    /// <summary>
    /// Queues one or more injected failures for the specified operation kind.
    /// </summary>
    /// <param name="kind">The operation kind to fail.</param>
    /// <param name="count">The number of consecutive occurrences to fail.</param>
    /// <param name="exceptionFactory">A factory producing the exception, or <see langword="null"/> for the default.</param>
    public void InjectFailure(ReminderTableOperationKind kind, int count = 1, Func<Exception>? exceptionFactory = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        var factory = exceptionFactory ?? (() => new ReminderTableUnavailableException($"{Name}: injected failure for {kind}."));
        lock (_gate)
        {
            if (!_injectedFailures.TryGetValue(kind, out var queue))
            {
                queue = new Queue<Func<Exception>>();
                _injectedFailures[kind] = queue;
            }

            for (var i = 0; i < count; i++)
            {
                queue.Enqueue(factory);
            }
        }
    }

    /// <summary>
    /// Removes every queued injected failure.
    /// </summary>
    public void ClearInjectedFailures()
    {
        lock (_gate)
        {
            _injectedFailures.Clear();
        }
    }

    /// <summary>
    /// Blocks the next operation of the specified kind until the returned gate is released.
    /// </summary>
    /// <param name="kind">The operation kind to block.</param>
    /// <returns>The gate.</returns>
    public ReminderTableOperationGate BlockNext(ReminderTableOperationKind kind)
    {
        var gate = new ReminderTableOperationGate(this, kind);
        lock (_gate)
        {
            _gates.Add(gate);
        }

        return gate;
    }

    /// <summary>
    /// Freezes reads against the current durable state, so subsequent reads observe a stale snapshot while writes
    /// continue to be applied.
    /// </summary>
    /// <returns>A handle which restores live reads when disposed.</returns>
    public IDisposable FreezeReads()
        => FreezeReadsCore(readKinds: null);

    /// <summary>
    /// Freezes reads of the specified kind against the current durable state, so matching reads observe a stale
    /// snapshot while writes and other read kinds continue against live state.
    /// </summary>
    /// <param name="kind">The read operation kind to freeze.</param>
    /// <returns>A handle which restores live reads when disposed.</returns>
    public IDisposable FreezeReads(ReminderTableOperationKind kind)
        => FreezeReadsCore([kind]);

    private IDisposable FreezeReadsCore(HashSet<ReminderTableOperationKind>? readKinds)
    {
        lock (_gate)
        {
            _frozenReads = new Dictionary<(GrainId, string), ReminderTableRecord>(_records);
            _frozenReadKinds = readKinds;
        }

        return new ReadFreeze(this);
    }

    internal void UnfreezeReads()
    {
        lock (_gate)
        {
            _frozenReads = null;
            _frozenReadKinds = null;
        }
    }

    internal void RemoveGate(ReminderTableOperationGate gate)
    {
        lock (_gate)
        {
            _gates.Remove(gate);
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // IReminderTable
    // ---------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await BeforeOperationAsync(ReminderTableOperationKind.Start, cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _started = true;
            Record(ReminderTableOperationKind.Start, null, null, null, null, null, null, true, 0, null);
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await BeforeOperationAsync(ReminderTableOperationKind.Stop, cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _started = false;
            Record(ReminderTableOperationKind.Stop, null, null, null, null, null, null, true, 0, null);
        }
    }

    /// <inheritdoc />
    public async Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName)
    {
        ArgumentNullException.ThrowIfNull(reminderName);
        await BeforeOperationAsync(ReminderTableOperationKind.ReadRow, grainId, reminderName);

        lock (_gate)
        {
            var source = GetReadSource(ReminderTableOperationKind.ReadRow);
            var found = source.TryGetValue((grainId, reminderName), out var record);
            Record(ReminderTableOperationKind.ReadRow, grainId, reminderName, null, null, null, record?.ETag, true, found ? 1 : 0, null);
            return found ? ToEntry(record!) : null;
        }
    }

    /// <inheritdoc />
    public async Task<ReminderTableData> ReadRows(GrainId grainId)
    {
        await BeforeOperationAsync(ReminderTableOperationKind.ReadGrainRows, grainId);

        lock (_gate)
        {
            var source = GetReadSource(ReminderTableOperationKind.ReadGrainRows);
            var matches = OrderRecords(source.Values.Where(record => record.GrainId.Equals(grainId))).Select(ToEntry).ToList();
            Record(ReminderTableOperationKind.ReadGrainRows, grainId, null, null, null, null, null, true, matches.Count, null);
            return new ReminderTableData(matches);
        }
    }

    /// <inheritdoc />
    public async Task<ReminderTableData> ReadRows(uint begin, uint end)
    {
        await BeforeOperationAsync(ReminderTableOperationKind.ReadRange, begin: begin, end: end);

        lock (_gate)
        {
            var source = GetReadSource(ReminderTableOperationKind.ReadRange);
            var matches = OrderRecords(source.Values.Where(record => InRange(record.UniformHashCode, begin, end))).Select(ToEntry).ToList();
            Record(ReminderTableOperationKind.ReadRange, null, null, begin, end, null, null, true, matches.Count, null);
            return new ReminderTableData(matches);
        }
    }

    /// <inheritdoc />
    public async Task<string?> UpsertRow(ReminderEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(entry.ReminderName);
        await BeforeOperationAsync(ReminderTableOperationKind.UpsertRow, entry.GrainId, entry.ReminderName);

        lock (_gate)
        {
            var key = (entry.GrainId, entry.ReminderName);
            _records.TryGetValue(key, out var existing);

            var etag = NextETag(entry.GrainId, entry.ReminderName);
            var record = new ReminderTableRecord(
                entry.GrainId,
                entry.ReminderName,
                entry.StartAt,
                entry.Period,
                etag,
                existing?.ETag,
                ++_versionCounter);

            CheckRecordInvariants(ReminderTableOperationKind.UpsertRow, key, record, existing);
            _records[key] = record;
            Record(ReminderTableOperationKind.UpsertRow, entry.GrainId, entry.ReminderName, null, null, entry.ETag, etag, true, 1, null);
            return etag;
        }
    }

    /// <inheritdoc />
    public async Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
    {
        ArgumentNullException.ThrowIfNull(reminderName);
        await BeforeOperationAsync(ReminderTableOperationKind.RemoveRow, grainId, reminderName);

        lock (_gate)
        {
            var key = (grainId, reminderName);
            var removed = _records.TryGetValue(key, out var existing)
                && existing is not null
                && string.Equals(existing.ETag, eTag, StringComparison.Ordinal);

            if (removed)
            {
                _records.Remove(key);
            }

            Record(ReminderTableOperationKind.RemoveRow, grainId, reminderName, null, null, eTag, existing?.ETag, removed, removed ? 1 : 0, null);
            return removed;
        }
    }

    /// <inheritdoc />
    public async Task TestOnlyClearTable()
    {
        await BeforeOperationAsync(ReminderTableOperationKind.ClearTable);

        lock (_gate)
        {
            var count = _records.Count;
            _records.Clear();
            _frozenReads = null;
            Record(ReminderTableOperationKind.ClearTable, null, null, null, null, null, null, true, count, null);
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Internals
    // ---------------------------------------------------------------------------------------------------------

    private async Task BeforeOperationAsync(
        ReminderTableOperationKind kind,
        GrainId? grainId = null,
        string? reminderName = null,
        uint? begin = null,
        uint? end = null,
        CancellationToken cancellationToken = default)
    {
        ReminderTableOperationGate? gate = null;
        lock (_gate)
        {
            for (var i = 0; i < _gates.Count; i++)
            {
                if (_gates[i].Kind == kind)
                {
                    gate = _gates[i];
                    _gates.RemoveAt(i);
                    break;
                }
            }
        }

        if (gate is not null)
        {
            gate.MarkBlocked();
            await gate.WaitForReleaseAsync(cancellationToken);
        }

        Func<Exception>? failure = null;
        bool available;
        lock (_gate)
        {
            available = _available;
            if (_injectedFailures.TryGetValue(kind, out var queue) && queue.Count > 0)
            {
                failure = queue.Dequeue();
            }
        }

        if (!available)
        {
            lock (_gate)
            {
                Record(kind, grainId, reminderName, begin, end, null, null, false, 0, nameof(ReminderTableUnavailableException));
            }

            throw new ReminderTableUnavailableException($"{Name}: the reminder table is unavailable (simulated storage outage) for {kind}.");
        }

        if (failure is not null)
        {
            var exception = failure();
            lock (_gate)
            {
                Record(kind, grainId, reminderName, begin, end, null, null, false, 0, exception.GetType().Name);
            }

            throw exception;
        }
    }

    private string NextETag(GrainId grainId, string reminderName)
    {
        var etag = $"etag-{(++_etagCounter).ToString("D6", CultureInfo.InvariantCulture)}";
        if (!_issuedETags.Add(etag))
        {
            ReminderFailureReport.Create(Name, nameof(IdealizedReminderTable), "UpsertRow")
                .WithIdentity(grainId, reminderName)
                .WithExpected("every issued ETag to be unique for the lifetime of the table")
                .WithObserved($"the ETag '{etag}' was issued twice")
                .WithETags(etag)
                .Throw();
        }

        return etag;
    }

    private void CheckRecordInvariants(
        ReminderTableOperationKind kind,
        (GrainId GrainId, string ReminderName) key,
        ReminderTableRecord record,
        ReminderTableRecord? existing)
    {
        if (!record.GrainId.Equals(key.GrainId) || !string.Equals(record.ReminderName, key.ReminderName, StringComparison.Ordinal))
        {
            ReminderFailureReport.Create(Name, nameof(CheckRecordInvariants), kind.ToString())
                .WithIdentity(key.GrainId, key.ReminderName)
                .WithExpected($"the stored record identity to equal its key ({key.GrainId}, '{key.ReminderName}')")
                .WithObserved($"({record.GrainId}, '{record.ReminderName}')")
                .Throw();
        }

        if (existing is not null && record.Version <= existing.Version)
        {
            ReminderFailureReport.Create(Name, nameof(CheckRecordInvariants), kind.ToString())
                .WithIdentity(key.GrainId, key.ReminderName)
                .WithExpected($"the record version to increase beyond {existing.Version.ToString(CultureInfo.InvariantCulture)}")
                .WithObserved($"version {record.Version.ToString(CultureInfo.InvariantCulture)}")
                .WithETags(record.ETag, existing.ETag)
                .Throw();
        }

        if (existing is not null && string.Equals(existing.ETag, record.ETag, StringComparison.Ordinal))
        {
            ReminderFailureReport.Create(Name, nameof(CheckRecordInvariants), kind.ToString())
                .WithIdentity(key.GrainId, key.ReminderName)
                .WithExpected("a write to replace the previous ETag")
                .WithObserved($"the ETag '{record.ETag}' was reused")
                .WithETags(record.ETag, existing.ETag)
                .Throw();
        }
    }

    private void Record(
        ReminderTableOperationKind kind,
        GrainId? grainId,
        string? reminderName,
        uint? begin,
        uint? end,
        string? suppliedETag,
        string? resultETag,
        bool succeeded,
        int resultCount,
        string? failure)
        => _operations.Add(new ReminderTableOperation(++_sequence, kind, grainId, reminderName, begin, end, suppliedETag, resultETag, succeeded, resultCount, failure));

    private static IEnumerable<ReminderTableRecord> OrderRecords(IEnumerable<ReminderTableRecord> records)
        => records
            .OrderBy(record => record.UniformHashCode)
            .ThenBy(record => record.GrainId.ToString(), StringComparer.Ordinal)
            .ThenBy(record => record.ReminderName, StringComparer.Ordinal);

    private Dictionary<(GrainId GrainId, string ReminderName), ReminderTableRecord> GetReadSource(ReminderTableOperationKind kind)
        => _frozenReads is not null && (_frozenReadKinds is null || _frozenReadKinds.Contains(kind))
            ? _frozenReads
            : _records;

    private static ReminderEntry ToEntry(ReminderTableRecord record) => new()
    {
        GrainId = record.GrainId,
        ReminderName = record.ReminderName,
        StartAt = record.StartAt,
        Period = record.Period,
        ETag = record.ETag
    };

    /// <summary>
    /// Determines whether a uniform hash code falls in the range (<paramref name="begin"/>, <paramref name="end"/>].
    /// </summary>
    /// <param name="hash">The uniform hash code.</param>
    /// <param name="begin">The exclusive lower bound.</param>
    /// <param name="end">The inclusive upper bound.</param>
    /// <returns><see langword="true"/> when the hash is in range.</returns>
    /// <remarks>When <paramref name="begin"/> is greater than or equal to <paramref name="end"/> the range wraps
    /// around zero and matches hashes above <paramref name="begin"/> or at or below <paramref name="end"/>.</remarks>
    public static bool InRange(uint hash, uint begin, uint end)
        => begin < end ? hash > begin && hash <= end : hash > begin || hash <= end;

    private sealed class ReadFreeze(IdealizedReminderTable owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.UnfreezeReads();
            }
        }
    }
}
