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
    private static readonly object NoOpCodec = new();

    public DurableNothing([ServiceKey] string key, IStateMachineManager manager)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        manager.RegisterStateMachine(key, this);
    }

    object IDurableStateMachine.OperationCodec => NoOpCodec;

    void IDurableStateMachine.Reset(LogStreamWriter writer) { }

    void IDurableStateMachine.Apply(ReadOnlySequence<byte> logEntry) { }

    void IDurableStateMachine.AppendEntries(LogStreamWriter writer) { }

    void IDurableStateMachine.AppendSnapshot(LogStreamWriter snapshotWriter) { }

    public IDurableStateMachine DeepCopy() => this;
}
