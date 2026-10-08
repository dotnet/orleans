using System;
using System.Buffers;

namespace Orleans.Serialization.Buffers;

/// <summary>
/// An immutable, garbage-collected snapshot of raw bytes.
/// </summary>
/// <remarks>
/// Borrowed inputs are copied, so they can be reused or released as soon as construction completes.
/// This type stores raw bytes; the application chooses the codecs for its values.
/// Ordinary garbage-collected references keep the storage alive for buffers and their memory views.
/// Consumers must treat the exposed read-only memory as immutable, including when using memory interop APIs.
/// </remarks>
[GenerateSerializer, Immutable]
public sealed class ImmutableBuffer
{
    [Id(0)]
    private readonly byte[] _bytes;

    /// <summary>
    /// Gets an empty buffer.
    /// </summary>
    public static ImmutableBuffer Empty { get; } = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ImmutableBuffer"/> class by copying the provided bytes.
    /// </summary>
    /// <param name="bytes">The borrowed bytes to copy.</param>
    public ImmutableBuffer(ReadOnlySpan<byte> bytes) => _bytes = bytes.ToArray();

    /// <summary>
    /// Initializes a new instance of the <see cref="ImmutableBuffer"/> class by copying the provided sequence.
    /// </summary>
    /// <param name="bytes">The borrowed sequence to copy.</param>
    /// <exception cref="ArgumentOutOfRangeException">The sequence length exceeds <see cref="int.MaxValue"/>.</exception>
    public ImmutableBuffer(ReadOnlySequence<byte> bytes)
    {
        if (bytes.Length > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), "The sequence is too large for a contiguous buffer.");
        }

        _bytes = bytes.IsEmpty ? Array.Empty<byte>() : new byte[(int)bytes.Length];
        bytes.CopyTo(_bytes);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ImmutableBuffer"/> class by copying the provided buffer.
    /// </summary>
    /// <param name="bytes">The borrowed buffer, which must remain valid until construction completes.</param>
    /// <remarks>This operation preserves the position and lifetime of <paramref name="bytes"/>.</remarks>
    public ImmutableBuffer(ArcBuffer bytes) => _bytes = bytes.ToArray();

    private ImmutableBuffer() => _bytes = Array.Empty<byte>();

    /// <summary>
    /// Gets read-only memory containing the bytes.
    /// </summary>
    public ReadOnlyMemory<byte> Memory => _bytes;

    /// <summary>
    /// Gets the number of bytes in this buffer.
    /// </summary>
    public int Length => _bytes.Length;

    /// <summary>
    /// Gets a single-segment sequence over the bytes without copying them.
    /// </summary>
    /// <returns>A sequence whose storage remains valid for as long as it is referenced.</returns>
    public ReadOnlySequence<byte> AsReadOnlySequence() => new(Memory);

    /// <summary>
    /// Writes raw bytes into a temporary writer and snapshots the written bytes once.
    /// </summary>
    /// <param name="write">A synchronous callback which writes and advances the supplied writer.</param>
    /// <returns>An immutable snapshot, or <see cref="Empty"/> if no bytes were written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="write"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The callback is invoked once. Exceptions propagate without returning a buffer.
    /// The result does not share storage with the writer or any memory obtained from it.
    /// </remarks>
    public static ImmutableBuffer Create(Action<IBufferWriter<byte>> write)
    {
        if (write is null)
        {
            throw new ArgumentNullException(nameof(write));
        }

        var writer = new ArrayBufferWriter<byte>();
        write(writer);
        return writer.WrittenCount == 0 ? Empty : new ImmutableBuffer(writer.WrittenSpan);
    }
}
