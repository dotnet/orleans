using System.Buffers;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling;

/// <summary>
/// A durable object which does nothing but preserves the raw state of a retired durable type.
/// </summary>
[DebuggerDisplay("DurableNothing")]
internal sealed class DurableNothing : IDurableStateMachine
{
    private readonly List<byte[]> _bufferedData = [];

    public string StateMachineKey { get; }
    public DateTime RetirementTimestamp { get; }

    public DurableNothing([ServiceKey] string key, DateTime retirementTimestamp, IStateMachineManager manager)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);

        manager.RegisterStateMachine(key, this);

        StateMachineKey = key;
        RetirementTimestamp = retirementTimestamp;
    }

    void IDurableStateMachine.Reset(IStateMachineLogWriter storage) => _bufferedData.Clear();
    void IDurableStateMachine.Apply(ReadOnlySequence<byte> logEntry) => _bufferedData.Add(logEntry.ToArray());

    void IDurableStateMachine.AppendEntries(StateMachineStorageWriter logWriter) { }

    void IDurableStateMachine.AppendSnapshot(StateMachineStorageWriter snapshotWriter)
    {
        // When a snapshot is created, we write the preserved raw data back out.
        foreach (var data in _bufferedData)
        {
            snapshotWriter.AppendEntry(data);
        }
    }

    public IDurableStateMachine DeepCopy() => this;
}
