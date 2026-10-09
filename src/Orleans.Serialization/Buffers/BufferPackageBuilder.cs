using System;
using System.Buffers;
using System.Collections.Generic;

namespace Orleans.Serialization.Buffers;

/// <summary>Builds a package using one pooled Arc writer and an ordinal key index.</summary>
/// <remarks>The builder is not thread-safe. Build transfers ownership and freezes the builder.</remarks>
public sealed class BufferPackageBuilder : IDisposable
{
    private readonly ArcBufferWriter _writer = new();
    private readonly Dictionary<string, (int Offset, int Length)> _entries = new(StringComparer.Ordinal);
    private bool _built;
    private bool _writing;
    private bool _disposed;

    /// <summary>Copies borrowed bytes directly into the package writer.</summary>
    /// <param name="key">A unique ordinal key, which may be empty.</param>
    /// <param name="bytes">Bytes to copy.</param>
    public void Add(string key, ReadOnlySpan<byte> bytes)
    {
        ValidateKey(key);
        var offset = _writer.Length;
        _ = checked(offset + bytes.Length);
        try
        {
            _writer.Write(bytes);
            _entries.Add(key, (offset, bytes.Length));
        }
        catch
        {
            _writer.Truncate(offset);
            throw;
        }
    }

    /// <summary>Writes an entry atomically using a scoped, independent temporary Arc writer.</summary>
    /// <param name="key">A unique ordinal key, which may be empty.</param>
    /// <param name="write">A synchronous callback invoked once after validating the key.</param>
    /// <remarks>
    /// Failed callbacks leave this builder unchanged. The callback cannot reenter this builder.
    /// Its writer and memory are valid only during the callback and must not be used afterwards.
    /// Written bytes are copied into the package writer, never directly shared with a frozen entry.
    /// </remarks>
    public void Add(string key, Action<IBufferWriter<byte>> write)
    {
        ValidateKey(key);
        if (write is null) throw new ArgumentNullException(nameof(write));
        using var temporary = new ArcBufferWriter();
        _writing = true;
        var offset = _writer.Length;
        try
        {
            write(temporary);
            _ = checked(offset + temporary.Length);
            using var bytes = temporary.PeekSlice(temporary.Length);
            bytes.CopyTo(_writer);
            _entries.Add(key, (offset, bytes.Length));
        }
        catch
        {
            _writer.Truncate(offset);
            throw;
        }
        finally
        {
            _writing = false;
        }
    }

    /// <summary>Transfers a consumed slice and the index to a package, then freezes the builder.</summary>
    /// <returns>A package which the caller must release.</returns>
    public BufferPackage Build()
    {
        ThrowIfUnavailable();
        var buffer = _writer.Length == 0 ? ArcBuffer.Empty : _writer.PeekSlice(_writer.Length);
        BufferPackage result;
        try { result = new BufferPackage(buffer, _entries); }
        catch { buffer.Dispose(); throw; }
        _writer.AdvanceReader(buffer.Length);
        _built = true;
        _writer.Dispose();
        return result;
    }

    /// <summary>Releases builder storage without releasing any package produced by Build.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        if (_writing) throw new InvalidOperationException("The builder cannot be disposed from a write callback.");
        _disposed = true;
        if (!_built) _writer.Dispose();
    }

    private void ValidateKey(string key)
    {
        ThrowIfUnavailable();
        if (key is null) throw new ArgumentNullException(nameof(key));
        if (_entries.ContainsKey(key)) throw new ArgumentException("The key has already been added.", nameof(key));
    }

    private void ThrowIfUnavailable()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BufferPackageBuilder));
        if (_built) throw new InvalidOperationException("The builder has already been built.");
        if (_writing) throw new InvalidOperationException("The builder cannot be used from a write callback.");
    }
}
