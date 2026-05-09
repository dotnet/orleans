using System.Buffers;

namespace Orleans.Journaling;

internal interface IOrleansBinaryJournalEntryCodec
{
    void Apply(ReadOnlySequence<byte> input, IJournaledState state);
}

internal static class DurableOperationHandler
{
    public static THandler GetRequiredHandler<THandler>(IJournaledState state, object codec)
    {
        if (state is THandler handler)
        {
            return handler;
        }

        throw new InvalidOperationException(
            $"State '{state.GetType().FullName}' is not compatible with codec '{codec.GetType().FullName}'.");
    }
}
