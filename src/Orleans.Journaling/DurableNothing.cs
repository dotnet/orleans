using System.Buffers;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling;

/// <summary>
/// A durable object which does nothing, used for retiring other durable types.
/// </summary>
[DebuggerDisplay("DurableNothing")]
internal sealed class DurableNothing : IDurableStateMachine
{
    public string StateMachineKey { get; }

    public DurableNothing([ServiceKey] string key, IStateMachineManager manager)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        manager.RegisterStateMachine(key, this);
        StateMachineKey = key;
    }

    void IDurableStateMachine.Reset(IStateMachineLogWriter storage) { }

    void IDurableStateMachine.Apply(ReadOnlySequence<byte> logEntry) { }

    void IDurableStateMachine.AppendEntries(StateMachineStorageWriter logWriter) { }

    void IDurableStateMachine.AppendSnapshot(StateMachineStorageWriter snapshotWriter) { }

    public IDurableStateMachine DeepCopy() => this;
}
