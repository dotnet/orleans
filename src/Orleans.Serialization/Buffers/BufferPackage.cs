using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace Orleans.Serialization.Buffers;

/// <summary>An owned Arc buffer with an ordinal index of keyed raw byte entries.</summary>
/// <remarks>
/// Views returned by this package are borrowed and must not be used after release.
/// Use <see cref="Retain"/> to obtain an independent owner. The underlying bytes must not be mutated.
/// </remarks>
public sealed class BufferPackage : IDisposable
{
    private ArcBuffer _buffer;
    private readonly Dictionary<string, (int Offset, int Length)> _entries;
    private int _released;

    internal BufferPackage(ArcBuffer buffer, Dictionary<string, (int Offset, int Length)> entries)
    {
        buffer.CheckValidity();
        if (!ReferenceEquals(entries.Comparer, StringComparer.Ordinal))
            throw new ArgumentException("Package keys must use ordinal comparison.", nameof(entries));
        foreach (var entry in entries.Values)
        {
            if (entry.Offset < 0 || entry.Length < 0 || entry.Offset > buffer.Length || entry.Length > buffer.Length - entry.Offset)
                throw new ArgumentOutOfRangeException(nameof(entries), "An entry lies outside the buffer.");
        }

        _buffer = buffer;
        _entries = entries;
    }

    private BufferPackage(ArcBuffer buffer, BufferPackage original)
    {
        _buffer = buffer;
        _entries = original._entries;
    }

    /// <summary>Gets a borrowed buffer view. Do not dispose this view or use it after release.</summary>
    public ArcBuffer Buffer { get { ThrowIfReleased(); return _buffer; } }

    /// <summary>Gets the number of entries, including empty entries.</summary>
    public int Count { get { ThrowIfReleased(); return _entries.Count; } }

    /// <summary>Gets the ordinal entry keys, with no guaranteed enumeration order.</summary>
    public IReadOnlyCollection<string> Keys { get { ThrowIfReleased(); return _entries.Keys; } }

    internal IReadOnlyDictionary<string, (int Offset, int Length)> Entries { get { ThrowIfReleased(); return _entries; } }

    /// <summary>Gets a borrowed byte sequence for a key, if present.</summary>
    /// <param name="key">An ordinal, case-sensitive key.</param>
    /// <param name="bytes">The borrowed bytes, or the default sequence if missing.</param>
    /// <returns>Whether the key exists, including entries with no bytes.</returns>
    public bool TryGetBytes(string key, out ReadOnlySequence<byte> bytes)
    {
        ThrowIfReleased();
        if (key is null) throw new ArgumentNullException(nameof(key));
        if (_entries.TryGetValue(key, out var entry))
        {
            bytes = _buffer.UnsafeSlice(entry.Offset, entry.Length).AsReadOnlySequence();
            return true;
        }

        bytes = default;
        return false;
    }

    /// <summary>Creates an independently owned package sharing the immutable bytes and read-only index.</summary>
    /// <returns>A package which must be released independently.</returns>
    public BufferPackage Retain()
    {
        ThrowIfReleased();
        var buffer = _buffer.Slice(0);
        try { return new BufferPackage(buffer, this); }
        catch { buffer.Dispose(); throw; }
    }

    /// <summary>Releases this package's pin. Repeated or concurrent calls have no effect.</summary>
    public void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0) return;
        _buffer.Dispose();
        _buffer = default;
    }

    /// <inheritdoc />
    public void Dispose() => Release();

    private void ThrowIfReleased()
    {
        if (Volatile.Read(ref _released) != 0) throw new ObjectDisposedException(nameof(BufferPackage));
    }
}
