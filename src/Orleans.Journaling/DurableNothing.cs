using System.Buffers;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling;

/// <summary>
/// A durable object which does nothing, used for retiring other durable types.
/// </summary>
public interface IDurableNothing
{
}

/// <summary>
/// A durable object which does nothing, used for retiring other durable types.
/// </summary>
internal sealed class DurableNothing : IDurableNothing, IDurableStateMachine
{
    public DurableNothing([ServiceKey] string key, IStateMachineManager manager)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        manager.RegisterStateMachine(key, this);
    }

    void IDurableStateMachine.Reset(IStateMachineLogWriter storage) { }

    void IDurableStateMachine.Apply(ReadOnlySequence<byte> logEntry) { }

    void IDurableStateMachine.AppendEntries(StateMachineLogWriter logWriter) { }

    void IDurableStateMachine.AppendSnapshot(StateMachineLogWriter snapshotWriter) { }

    public IDurableStateMachine DeepCopy() => this;
}
