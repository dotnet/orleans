using System;
using System.Buffers;
using System.Collections.Generic;

namespace Orleans.Serialization.Buffers;

/// <summary>
/// Builds an immutable <see cref="BufferPackage"/> from keyed raw byte entries.
/// </summary>
/// <remarks>
/// This builder is not thread-safe. Keys use ordinal, case-sensitive comparison and may be empty.
/// A successful <see cref="Build"/> freezes the builder; it cannot be built again or accept more entries.
/// </remarks>
public sealed class BufferPackageBuilder
{
    private readonly ArrayBufferWriter<byte> _writer = new();
    private readonly Dictionary<string, (int Offset, int Length)> _entries = new(StringComparer.Ordinal);
    private bool _built;
    private bool _writing;

    /// <summary>
    /// Adds an entry by copying borrowed bytes into the builder.
    /// </summary>
    /// <param name="key">The unique entry key.</param>
    /// <param name="bytes">The borrowed bytes, which can be reused after this method returns.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The key has already been added.</exception>
    /// <exception cref="InvalidOperationException">The builder is frozen or a write callback is in progress.</exception>
    /// <exception cref="OverflowException">The combined byte length exceeds <see cref="int.MaxValue"/>.</exception>
    public void Add(string key, ReadOnlySpan<byte> bytes)
    {
        ValidateKey(key);
        Append(key, bytes);
    }

    /// <summary>
    /// Adds an entry by invoking a callback with an independent temporary writer, then copying its written bytes.
    /// </summary>
    /// <param name="key">The unique entry key.</param>
    /// <param name="write">A synchronous callback which writes and advances the supplied writer.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> or <paramref name="write"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The key has already been added.</exception>
    /// <exception cref="InvalidOperationException">The builder is frozen or a write callback is in progress.</exception>
    /// <exception cref="OverflowException">The combined byte length exceeds <see cref="int.MaxValue"/>.</exception>
    /// <remarks>
    /// The callback is invoked once, after validating the key. It cannot add entries or build this builder.
    /// If it throws, the exception propagates and the builder remains unchanged, so the key can be retried.
    /// Memory obtained from the callback's writer is never shared with another entry or the resulting package.
    /// </remarks>
    public void Add(string key, Action<IBufferWriter<byte>> write)
    {
        ValidateKey(key);
        if (write is null)
        {
            throw new ArgumentNullException(nameof(write));
        }

        var writer = new ArrayBufferWriter<byte>();
        _writing = true;
        try
        {
            write(writer);
        }
        finally
        {
            _writing = false;
        }

        Append(key, writer.WrittenSpan);
    }

    /// <summary>
    /// Snapshots the accumulated bytes, transfers the validated index, and freezes this builder.
    /// </summary>
    /// <returns>A package containing all entries, including empty entries.</returns>
    /// <exception cref="InvalidOperationException">The builder is frozen or a write callback is in progress.</exception>
    /// <remarks>Discard the builder after building to release its temporary storage.</remarks>
    public BufferPackage Build()
    {
        ThrowIfUnavailable();
        var buffer = _writer.WrittenCount == 0 ? ImmutableBuffer.Empty : new ImmutableBuffer(_writer.WrittenSpan);
        var result = new BufferPackage(buffer, _entries);
        _built = true;
        return result;
    }

    private void Append(string key, ReadOnlySpan<byte> bytes)
    {
        var offset = _writer.WrittenCount;
        _ = checked(offset + bytes.Length);
        if (!bytes.IsEmpty)
        {
            bytes.CopyTo(_writer.GetSpan(bytes.Length));
        }

        // Publish the index before advancing. A failed allocation cannot leave unindexed written bytes.
        _entries.Add(key, (offset, bytes.Length));
        _writer.Advance(bytes.Length);
    }

    private void ValidateKey(string key)
    {
        ThrowIfUnavailable();
        if (key is null)
        {
            throw new ArgumentNullException(nameof(key));
        }

        if (_entries.ContainsKey(key))
        {
            throw new ArgumentException("The key has already been added.", nameof(key));
        }
    }

    private void ThrowIfUnavailable()
    {
        if (_built)
        {
            throw new InvalidOperationException("The builder has already been built.");
        }

        if (_writing)
        {
            throw new InvalidOperationException("The builder cannot be used from a write callback.");
        }
    }
}
