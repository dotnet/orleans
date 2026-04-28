using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Writes the payload for one pending log entry.
/// </summary>
/// <remarks>
/// Instances are owned and reused by the journaling runtime. Codecs must not retain this instance,
/// or any memory returned by it, after the write call returns.
/// </remarks>
public sealed class LogEntryWriter : IBufferWriter<byte>
{
    private ILogEntryWriterTarget? _target;
    private ILogEntryWriterCompletion? _completion;
    private int _entryStart;

    internal LogEntryWriter()
    {
    }

    /// <inheritdoc/>
    public void Advance(int count) => GetTarget().Advance(count);

    /// <inheritdoc/>
    public Memory<byte> GetMemory(int sizeHint = 0) => GetTarget().GetMemory(sizeHint);

    /// <inheritdoc/>
    public Span<byte> GetSpan(int sizeHint = 0) => GetTarget().GetSpan(sizeHint);

    /// <summary>
    /// Writes the provided bytes to the current log entry.
    /// </summary>
    public void Write(ReadOnlySpan<byte> value) => GetTarget().Write(value);

    /// <summary>
    /// Writes a LEB128-encoded unsigned 32-bit integer to the current log entry.
    /// </summary>
    public void WriteVarUInt32(uint value) => VarIntHelper.WriteVarUInt32(this, value);

    /// <summary>
    /// Writes a LEB128-encoded unsigned 64-bit integer to the current log entry.
    /// </summary>
    public void WriteVarUInt64(ulong value) => VarIntHelper.WriteVarUInt64(this, value);

    internal void Initialize(ILogEntryWriterTarget target, int entryStart, ILogEntryWriterCompletion? completion = null)
    {
        if (_target is not null)
        {
            throw new InvalidOperationException("The log entry writer is already writing an entry.");
        }

        _target = target;
        _entryStart = entryStart;
        _completion = completion;
    }

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

    private ILogEntryWriterTarget GetTarget()
    {
        if (_target is null)
        {
            throw new InvalidOperationException("The log entry writer is not writing an entry.");
        }

        return _target;
    }

    private void Complete()
    {
        var completion = _completion;
        _target = null;
        _completion = null;
        _entryStart = 0;
        completion?.CompleteEntryWrite();
    }
}

internal interface ILogEntryWriterTarget : IBufferWriter<byte>
{
    void Write(ReadOnlySpan<byte> value);
    void CommitEntry(int entryStart);
    void AbortEntry(int entryStart);
}

internal interface ILogEntryWriterCompletion
{
    void CompleteEntryWrite();
}
