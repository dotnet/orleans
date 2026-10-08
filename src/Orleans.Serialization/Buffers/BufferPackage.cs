using System;
using System.Collections.Generic;

namespace Orleans.Serialization.Buffers;

/// <summary>
/// An immutable collection of independently addressable raw byte entries in one buffer.
/// </summary>
/// <remarks>
/// Create packages using <see cref="BufferPackageBuilder"/>. Keys use ordinal, case-sensitive comparison.
/// Entries store raw bytes as slices of one <see cref="ImmutableBuffer"/>.
/// The application chooses the codecs for its values. Ordinary garbage-collected references
/// keep the storage alive for the package and its entry memory views.
/// </remarks>
[GenerateSerializer, Immutable]
public sealed class BufferPackage
{
    [Id(0)]
    private readonly ImmutableBuffer _buffer;

    [Id(1)]
    private readonly Dictionary<string, (int Offset, int Length)> _entries;

    internal BufferPackage(ImmutableBuffer buffer, Dictionary<string, (int Offset, int Length)> entries)
    {
        _buffer = buffer;
        _entries = entries;
    }

    /// <summary>
    /// Gets the buffer containing the concatenated entry bytes.
    /// </summary>
    public ImmutableBuffer Buffer => _buffer;

    /// <summary>
    /// Gets the number of entries, including entries with no bytes.
    /// </summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Gets a read-only collection of the entry keys, with no guaranteed enumeration order.
    /// </summary>
    public IReadOnlyCollection<string> Keys => _entries.Keys;

    /// <summary>
    /// Attempts to get the bytes for the specified key without copying them.
    /// </summary>
    /// <param name="key">The key to look up using ordinal, case-sensitive comparison.</param>
    /// <param name="bytes">The entry bytes if found; otherwise, the default read-only memory.</param>
    /// <returns><see langword="true"/> if the key exists, including for an empty entry; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    public bool TryGetBytes(string key, out ReadOnlyMemory<byte> bytes)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            bytes = _buffer.Memory.Slice(entry.Offset, entry.Length);
            return true;
        }

        bytes = default;
        return false;
    }
}
