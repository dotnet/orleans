using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;

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
/// Each append, removal, and clear marks the activation's journal modified, so the logs report every change they stage.
/// </para>
/// </remarks>
internal sealed class DurableTaskAccessLogs : IStateMachine, IReportsStagedChanges, IDurableDictionaryCommandHandler<TaskId, byte[]>
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

    /// <summary>Creates the access logs of <paramref name="manager"/>'s activation, which journal through it.</summary>
    public DurableTaskAccessLogs(IJournaledStateManager manager, IDurableDictionaryCommandCodec<TaskId, byte[]> codec)
    {
        _codec = codec;
        manager.RegisterStateMachine(StateMachineName, this);
    }

    /// <summary>Creates the access logs of the activation whose services <paramref name="services"/> resolves.</summary>
    public static DurableTaskAccessLogs Create(IServiceProvider services) =>
        new(
            services.GetRequiredService<IJournaledStateManager>(),
            JournalFormatServices.GetRequiredCommandCodec<IDurableDictionaryCommandCodec<TaskId, byte[]>>(
                services,
                services.GetRequiredService<JournaledStateManagerShared>().JournalFormatKey));

    public byte[] Get(TaskId taskId) => _logs.TryGetValue(taskId, out var log) ? log.Records.AsSpan(0, log.Length).ToArray() : [];

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

    void IStateMachine.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
        context.GetRequiredCommandCodec(entry.FormatKey, _codec).Apply(entry.Reader, this);

    void IStateMachine.WritePendingEntries(JournalStreamWriter writer)
    {
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
