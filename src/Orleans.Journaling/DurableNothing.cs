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
internal sealed class DurableNothing : IDurableNothing, IJournaledState
{
    public DurableNothing([ServiceKey] string key, IStateManager manager)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        manager.RegisterState(key, this);
    }

    Type IJournaledState.OperationCodecServiceType => typeof(object);

    void IJournaledState.Reset(JournalStreamWriter writer) { }

    void IJournaledState.AppendEntries(JournalStreamWriter writer) { }

    void IJournaledState.AppendSnapshot(JournalStreamWriter snapshotWriter) { }

    public IJournaledState DeepCopy() => this;
}
