using System.Buffers;

namespace Orleans.Journaling;

internal static class AzureBlobJournalStorageStreamHelpers
{
    // SkipStreamAsync caps each non-seekable drain read at this size instead of using larger pooled arrays.
    private const int MaxSkipBufferBytes = 16 * 1024;

    internal static async ValueTask SkipStreamAsync(Stream input, long length, CancellationToken cancellationToken)
    {
        // Used during recovery to consume checkpoint-covered WAL bytes without exposing them to the consumer.
        if (length <= 0)
        {
            return;
        }

        if (input.CanSeek)
        {
            // Seeking is only used when supported; Azure response streams are often forward-only.
            input.Seek(length, SeekOrigin.Current);
            return;
        }

        // Non-seekable streams can only advance by reading, so drain discarded bytes into a pooled buffer.
        var maxChunkSize = (int)Math.Min(MaxSkipBufferBytes, length);
        var buffer = ArrayPool<byte>.Shared.Rent(maxChunkSize);

        try
        {
            while (length > 0)
            {
                // ArrayPool can return a larger array, so slice it to keep each skip read capped.
                var toRead = (int)Math.Min(maxChunkSize, length);

                // ReadExactlyAsync guarantees the buffer slice is completely filled before returning,
                // or it throws an EndOfStreamException if the stream ends early.
                await input.ReadExactlyAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);

                length -= toRead;
            }
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidOperationException("Azure Blob journal WAL ended before the checkpoint offset was reached.", ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
