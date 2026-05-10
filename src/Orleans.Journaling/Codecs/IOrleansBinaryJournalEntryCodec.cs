using Orleans.Serialization.Buffers;
using Orleans.Serialization.Buffers.Adaptors;

namespace Orleans.Journaling;

internal interface IOrleansBinaryJournalEntryCodec
{
    /// <summary>
    /// Applies a single binary journal operation to <paramref name="state"/>.
    /// </summary>
    /// <param name="reader">A reader positioned at the start of the operation command. Implementations must advance the reader past the entire operation body.</param>
    /// <param name="state">The target state.</param>
    void Apply(ref Reader<ArcBufferReaderInput> reader, IJournaledState state);
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
