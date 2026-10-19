using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableTasks.Runtime;
using Orleans.Journaling;
using Orleans.Serialization;

namespace Orleans.DurableTasks.Storage;

/// <summary>
/// Journals the access logs of durable task executions, which record the executions' accesses to journaled state.
/// </summary>
/// <remarks>
/// <para>
/// Appends are staged in memory and journaled lazily: each journal write journals, for every execution whose log grew,
/// one entry holding the records appended since the previous write, and a removal for every journaled log which was
/// removed. Records are opaque bytes, which a JSON journal holds as base64. A snapshot journals each log whole.
/// </para>
/// <para>
/// The entries reuse the durable dictionary commands of <c>TaskId</c> keys and <c>byte[]</c> values, where a set appends
/// its value to the execution's log.
/// </para>
/// <para>
/// The logs hold the journal's positional reads: records of reads as of a position of the journal which stores them. While
/// any log, or any running attempt's records which aren't staged yet, holds one, the logs hold a compaction barrier on the
/// journal. When the journal resolves them, at recovery or before a snapshot which would replace their history, each
/// becomes a record of the read's value, and a resolved log is journaled whole, as a removal followed by a set.
/// </para>
/// <para>
/// Each append, removal, clear, and resolution which rewrites a log marks the activation's journal modified, so the logs
/// report every change they stage.
/// </para>
/// </remarks>
internal sealed class DurableTaskAccessLogs : IStateMachine, IReportsStagedChanges, IDurableDictionaryCommandHandler<TaskId, byte[]>, IJournalPositionHolder
{
    /// <summary>The journal state machine name of the access logs.</summary>
    public const string StateMachineName = "$task-access-logs";

    private readonly IDurableDictionaryCommandCodec<TaskId, byte[]> _codec;

    private readonly Dictionary<TaskId, Log> _logs = [];

    // The logs which grew since the previous write, and the journaled logs which were removed since then.
    private readonly HashSet<TaskId> _appended = [];
    private readonly HashSet<TaskId> _removed = [];
    private bool _cleared;
    private JournalStreamWriter _writer;

    // The attempts which hold records that they haven't staged, which every write stages before it captures.
    private readonly List<IDurableTaskPendingAccessRecords> _pendingSources = [];

    // The serializer of positional reads' arguments and results; without one, reads record values.
    private readonly Serializer? _serializer;

    // The running attempts which recorded positional reads, until they close: their records which aren't staged yet hold
    // positional reads too.
    private readonly HashSet<DurableTaskAccessLog> _recorders = [];

    /// <summary>Creates the access logs of <paramref name="manager"/>'s activation, which journal through it.</summary>
    /// <param name="manager">The manager which journals the logs.</param>
    /// <param name="codec">The codec of the logs' entries.</param>
    /// <param name="serializer">
    /// The serializer of positional reads' arguments and results, or <see langword="null"/> when every read records its value.
    /// </param>
    public DurableTaskAccessLogs(IJournaledStateManager manager, IDurableDictionaryCommandCodec<TaskId, byte[]> codec, Serializer? serializer = null)
    {
        _codec = codec;
        _serializer = serializer;
        Journal = serializer is null ? null : manager as JournaledStateManager;
        manager.RegisterStateMachine(StateMachineName, this);
    }

    /// <summary>Gets the journal whose positions the logs' reads can record, or <see langword="null"/>.</summary>
    public JournaledStateManager? Journal { get; }

    /// <inheritdoc/>
    /// <remarks>
    /// Derived from the records: a log, or the pending records of an open attempt which recorded positions, holds a
    /// positional record. An attempt which closes discards its pending records, so they hold nothing from then on.
    /// </remarks>
    public bool HoldsPositionalReads
    {
        get
        {
            foreach (var log in _logs.Values)
            {
                if (DurableTaskAccessLog.FindPositionalRecords(log.Records.AsMemory(0, log.Length), []) is not null)
                {
                    return true;
                }
            }

            foreach (var recorder in _recorders)
            {
                if (DurableTaskAccessLog.FindPositionalRecords(recorder.PendingRecords, recorder.PendingStates) is not null)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Creates the access logs of the activation whose services <paramref name="services"/> resolves.</summary>
    public static DurableTaskAccessLogs Create(IServiceProvider services) =>
        new(
            services.GetRequiredService<IJournaledStateManager>(),
            JournalFormatServices.GetRequiredCommandCodec<IDurableDictionaryCommandCodec<TaskId, byte[]>>(
                services,
                services.GetRequiredService<JournaledStateManagerShared>().JournalFormatKey),
            services.GetRequiredService<Serializer>());

    // Appends never change bytes which a log already holds, so the records which this returns stay as they are.
    public ReadOnlyMemory<byte> Get(TaskId taskId) => _logs.TryGetValue(taskId, out var log) ? new(log.Records, 0, log.Length) : default;

    public void Append(TaskId taskId, ReadOnlySpan<byte> records)
    {
        if (records.IsEmpty)
        {
            return;
        }

        if (!_logs.TryGetValue(taskId, out var log))
        {
            _logs[taskId] = log = new();
        }

        log.Append(records);
        _appended.Add(taskId);
        _writer.MarkModified();
    }

    /// <summary>Stages <paramref name="source"/>'s records before the next write captures.</summary>
    /// <remarks>
    /// A pending read's record stages no change of the attempt's journaled state: it records what the attempt observed, and
    /// commits with whichever write captures next. It therefore doesn't mark the journal modified, and its append as a
    /// write captures, like the capture itself, stages nothing new.
    /// </remarks>
    public void AddPendingRecords(IDurableTaskPendingAccessRecords source) => _pendingSources.Add(source);

    // The activation runs one work item at a time, so no access is in progress while a write captures.
    private void StagePendingRecords()
    {
        foreach (var source in _pendingSources)
        {
            source.Flush();
        }

        _pendingSources.Clear();
    }

    /// <summary>Notes that the open attempt <paramref name="recorder"/> records positional reads.</summary>
    /// <remarks>The logs collect and resolve its pending records until it closes or is discarded.</remarks>
    public void OnRecorderOpened(DurableTaskAccessLog recorder)
    {
        Debug.Assert(recorder.IsOpen, "Only an open attempt records.");
        _recorders.Add(recorder);
    }

    /// <summary>Holds the journal's barrier for a positional read as of <paramref name="position"/>, unless it holds one.</summary>
    /// <remarks>
    /// The barrier stays while no execution holds positional reads, so executions which come and go journal no barrier
    /// commands; the journal releases an idle barrier before a snapshot.
    /// </remarks>
    public void OnPositionalRecord(long position) => Journal!.Positions.HoldBarrier(StateMachineName, position);

    /// <summary>
    /// Notes that an attempt stopped recording: its pending records are discarded, and its staged records stay in its
    /// execution's log.
    /// </summary>
    public void OnRecorderClosed(DurableTaskAccessLog recorder) => _recorders.Remove(recorder);

    public void Remove(TaskId taskId)
    {
        if (!_logs.Remove(taskId, out var log))
        {
            return;
        }

        _appended.Remove(taskId);
        if (log.JournaledLength > 0)
        {
            _removed.Add(taskId);
        }

        _writer.MarkModified();
    }

    public void Clear()
    {
        _cleared = true;
        _logs.Clear();
        _appended.Clear();
        _removed.Clear();
        _writer.MarkModified();
    }

    void IStateMachine.Reset(JournalStreamWriter writer)
    {
        _writer = writer;
        _logs.Clear();
        _appended.Clear();
        _removed.Clear();
        _cleared = false;
    }

    JournalPositionalReads? IJournalPositionHolder.CollectPositionalReads(long fromPosition)
    {
        JournalPositionalReads? reads = null;
        foreach (var log in _logs.Values)
        {
            Collect(log.Records.AsMemory(0, log.Length), [], fromPosition, ref reads);
        }

        foreach (var recorder in _recorders)
        {
            Collect(recorder.PendingRecords, recorder.PendingStates, fromPosition, ref reads);
        }

        return reads;

        void Collect(ReadOnlyMemory<byte> records, ReadOnlySpan<int> states, long fromPosition, ref JournalPositionalReads? reads)
        {
            if (DurableTaskAccessLog.FindPositionalRecords(records, states) is not { } found)
            {
                return;
            }

            foreach (var record in found)
            {
                if (record.Position >= fromPosition)
                {
                    (reads ??= new(_serializer!)).Add(record.Position, record.StateId, record.Operation, record.Argument);
                }
            }
        }
    }

    void IJournalPositionHolder.ApplyResolvedReads(JournalPositionalReads reads)
    {
        // A read's outcome depends only on its position, state, operation, and argument, so records are matched by those,
        // wherever attempts moved them since they were collected.
        var resolved = new Dictionary<ReadKey, int>(reads.Count);
        for (var i = 0; i < reads.Count; i++)
        {
            if (reads.TryGetResult(i, out _))
            {
                var read = reads[i];
                resolved.TryAdd(new(read.Position, read.StateId, read.Operation, read.Argument), i);
            }
        }

        if (resolved.Count == 0)
        {
            return;
        }

        List<(TaskId TaskId, Log Log, byte[] Records)>? rewrittenLogs = null;
        foreach (var (taskId, log) in _logs)
        {
            if (Rewrite(log.Records.AsSpan(0, log.Length), log.Records.AsMemory(0, log.Length), [], resolved, reads) is { } rewritten)
            {
                (rewrittenLogs ??= []).Add((taskId, log, rewritten));
            }
        }

        foreach (var (taskId, log, records) in rewrittenLogs ?? [])
        {
            _logs[taskId] = new Log(records);
            if (log.JournaledLength > 0)
            {
                _removed.Add(taskId);
            }

            _appended.Add(taskId);
            _writer.MarkModified();
        }

        foreach (var recorder in _recorders)
        {
            var pending = recorder.PendingRecords;
            if (Rewrite(pending.Span, pending, recorder.PendingStates, resolved, reads) is { } rewritten)
            {
                recorder.ReplacePendingRecords(rewritten);
            }
        }
    }

    // Returns the records with each resolved positional record replaced by a record of its value, or null if none was.
    private static byte[]? Rewrite(
        ReadOnlySpan<byte> records,
        ReadOnlyMemory<byte> memory,
        ReadOnlySpan<int> states,
        Dictionary<ReadKey, int> resolved,
        JournalPositionalReads reads)
    {
        if (DurableTaskAccessLog.FindPositionalRecords(memory, states) is not { } found)
        {
            return null;
        }

        ArrayBufferWriter<byte>? output = null;
        var copied = 0;
        foreach (var record in found)
        {
            if (!resolved.TryGetValue(new(record.Position, record.StateId, record.Operation, record.Argument), out var index)
                || !reads.TryGetResult(index, out var value))
            {
                continue;
            }

            output ??= new(records.Length + 64);
            output.Write(records[copied..record.Start]);
            DurableTaskAccessLog.WriteValueRecord(output, records, record, value);
            copied = record.End;
        }

        if (output is null)
        {
            return null;
        }

        output.Write(records[copied..]);
        return output.WrittenSpan.ToArray();
    }

    private readonly struct ReadKey(long position, int stateId, JournaledStateOperation operation, ReadOnlyMemory<byte> argument) : IEquatable<ReadKey>
    {
        private readonly long _position = position;
        private readonly int _stateId = stateId;
        private readonly JournaledStateOperation _operation = operation;
        private readonly ReadOnlyMemory<byte> _argument = argument;

        public bool Equals(ReadKey other) =>
            _position == other._position && _stateId == other._stateId && _operation == other._operation && _argument.Span.SequenceEqual(other._argument.Span);

        public override bool Equals(object? obj) => obj is ReadKey other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(_position);
            hash.Add(_stateId);
            hash.Add(_operation);
            hash.AddBytes(_argument.Span);
            return hash.ToHashCode();
        }
    }

    void IStateMachine.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
        context.GetRequiredCommandCodec(entry.FormatKey, _codec).Apply(entry.Reader, this);

    void IStateMachine.WritePendingEntries(JournalStreamWriter writer)
    {
        StagePendingRecords();
        if (_cleared)
        {
            _codec.WriteClear(writer);
            _cleared = false;
        }

        foreach (var taskId in _removed)
        {
            _codec.WriteRemove(taskId, writer);
        }

        _removed.Clear();
        foreach (var taskId in _appended)
        {
            var log = _logs[taskId];
            _codec.WriteSet(taskId, log.Records.AsSpan(log.JournaledLength, log.Length - log.JournaledLength).ToArray(), writer);
            log.JournaledLength = log.Length;
        }

        _appended.Clear();
    }

    void IStateMachine.WriteSnapshot(JournalStreamWriter writer)
    {
        StagePendingRecords();
        _codec.WriteSnapshot(
            [.. _logs.Select(static pair => KeyValuePair.Create(pair.Key, pair.Value.Records.AsSpan(0, pair.Value.Length).ToArray()))],
            writer);
        foreach (var log in _logs.Values)
        {
            log.JournaledLength = log.Length;
        }

        _appended.Clear();
        _removed.Clear();
        _cleared = false;
    }

    // A set appends its records to the execution's log.
    void IDurableDictionaryCommandHandler<TaskId, byte[]>.ApplySet(TaskId key, byte[] value)
    {
        if (!_logs.TryGetValue(key, out var log))
        {
            _logs[key] = log = new();
        }

        log.Append(value);
        log.JournaledLength = log.Length;
    }

    void IDurableDictionaryCommandHandler<TaskId, byte[]>.ApplyRemove(TaskId key) => _logs.Remove(key);

    void IDurableDictionaryCommandHandler<TaskId, byte[]>.ApplyClear() => _logs.Clear();

    void IDurableDictionaryCommandHandler<TaskId, byte[]>.Reset(int capacityHint)
    {
        _logs.Clear();
        _logs.EnsureCapacity(capacityHint);
    }

    private sealed class Log
    {
        public Log()
        {
        }

        // A log whose records replace a journaled log's: it journals whole.
        public Log(byte[] records)
        {
            Records = records;
            Length = records.Length;
        }

        public byte[] Records { get; private set; } = [];

        public int Length { get; private set; }

        // The number of bytes which earlier writes journaled.
        public int JournaledLength { get; set; }

        public void Append(ReadOnlySpan<byte> records)
        {
            if (Length + records.Length > Records.Length)
            {
                var grown = new byte[Math.Max(Length + records.Length, Records.Length * 2)];
                Records.AsSpan(0, Length).CopyTo(grown);
                Records = grown;
            }

            records.CopyTo(Records.AsSpan(Length));
            Length += records.Length;
        }
    }
}
