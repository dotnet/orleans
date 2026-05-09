using System.Buffers;

namespace Orleans.Journaling;

internal interface IOrleansBinaryJournalEntryCodec
{
    void Apply(ReadOnlySequence<byte> input, IDurableStateMachine stateMachine);
}

internal static class DurableOperationHandler
{
    public static THandler GetRequiredHandler<THandler>(IDurableStateMachine stateMachine, object codec)
    {
        if (stateMachine is THandler handler)
        {
            return handler;
        }

        throw new InvalidOperationException(
            $"State machine '{stateMachine.GetType().FullName}' is not compatible with codec '{codec.GetType().FullName}'.");
    }
}
