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

    void IDurableStateMachine.Reset(JournalStreamWriter writer) { }

    void IDurableStateMachine.AppendEntries(JournalStreamWriter writer) { }

    void IDurableStateMachine.AppendSnapshot(JournalStreamWriter snapshotWriter) { }

    public IDurableStateMachine DeepCopy() => this;
}
