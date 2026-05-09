using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Writes the payload for one pending journal entry.
/// </summary>
/// <remarks>
/// Instances are owned and reused by the journaling runtime. Codecs must not retain this instance,
/// or any memory returned by it, after the write call returns.
/// </remarks>
public sealed class JournalEntryWriter : IBufferWriter<byte>
{
    private IJournalEntryWriterTarget? _target;
    private IJournalEntryWriterCompletion? _completion;
    private int _entryStart;
    private bool _completed;

    internal JournalEntryWriter()
    {
    }

    /// <inheritdoc/>
    public void Advance(int count) => GetTarget().Advance(count);

    /// <inheritdoc/>
    public Memory<byte> GetMemory(int sizeHint = 0) => GetTarget().GetMemory(sizeHint);

    /// <inheritdoc/>
    public Span<byte> GetSpan(int sizeHint = 0) => GetTarget().GetSpan(sizeHint);

    /// <summary>
    /// Writes the provided bytes to the current journal entry.
    /// </summary>
    public void Write(ReadOnlySpan<byte> value) => GetTarget().Write(value);

    /// <summary>
    /// Writes the provided bytes to the current journal entry.
    /// </summary>
    public void Write(ReadOnlySequence<byte> value) => GetTarget().Write(value);

    /// <summary>
    /// Writes an Orleans.Serialization-encoded unsigned 32-bit integer to the current journal entry.
    /// </summary>
    public void WriteVarUInt32(uint value) => VarIntHelper.WriteVarUInt32(this, value);

    /// <summary>
    /// Writes an Orleans.Serialization-encoded unsigned 64-bit integer to the current journal entry.
    /// </summary>
    public void WriteVarUInt64(ulong value) => VarIntHelper.WriteVarUInt64(this, value);

    internal void Initialize(IJournalEntryWriterTarget target, int entryStart, IJournalEntryWriterCompletion? completion = null)
    {
        if (_target is not null)
        {
            throw new InvalidOperationException("The journal entry writer is already writing an entry.");
        }

        _target = target;
        _entryStart = entryStart;
        _completion = completion;
        _completed = false;
    }

    internal bool IsActive => _target is not null;

    internal void Commit()
    {
        var target = GetTarget();
        target.CommitEntry(_entryStart);
        Complete();
    }

    internal void Abort()
    {
        var target = GetTarget();
        try
        {
            target.AbortEntry(_entryStart);
        }
        finally
        {
            Complete();
        }
    }

    private IJournalEntryWriterTarget GetTarget()
    {
        if (_target is null)
        {
            if (_completed)
            {
                throw new InvalidOperationException("The journal entry has already completed.");
            }

            throw new InvalidOperationException("The journal entry writer is not writing an entry.");
        }

        return _target;
    }

    private void Complete()
    {
        var completion = _completion;
        _target = null;
        _completion = null;
        _entryStart = 0;
        _completed = true;
        completion?.CompleteEntryWrite();
    }
}

internal interface IJournalEntryWriterTarget : IBufferWriter<byte>
{
    void Write(ReadOnlySpan<byte> value);
    void Write(ReadOnlySequence<byte> value);
    void CommitEntry(int entryStart);
    void AbortEntry(int entryStart);
}

internal interface IJournalEntryWriterCompletion
{
    void CompleteEntryWrite();
}
