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
    private JournalBatchWriterBase? _target;
    private int _entryStart;
    private bool _completed;

    internal JournalEntryWriter()
    {
    }

    /// <inheritdoc/>
    public void Advance(int count) => GetTarget().AdvanceEntryPayload(count);

    /// <inheritdoc/>
    public Memory<byte> GetMemory(int sizeHint = 0) => GetTarget().GetEntryPayloadMemory(sizeHint);

    /// <inheritdoc/>
    public Span<byte> GetSpan(int sizeHint = 0) => GetTarget().GetEntryPayloadSpan(sizeHint);

    /// <summary>
    /// Writes the provided bytes to the current journal entry.
    /// </summary>
    public void Write(ReadOnlySpan<byte> value) => GetTarget().WriteEntryPayload(value);

    /// <summary>
    /// Writes the provided bytes to the current journal entry.
    /// </summary>
    public void Write(ReadOnlySequence<byte> value) => GetTarget().WriteEntryPayload(value);

    internal void Initialize(JournalBatchWriterBase target, int entryStart)
    {
        if (_target is not null)
        {
            throw new InvalidOperationException("The journal entry writer is already writing an entry.");
        }

        _target = target;
        _entryStart = entryStart;
        _completed = false;
    }

    internal bool IsActive => _target is not null;

    internal void Commit()
    {
        var target = GetTarget();
        target.CommitEntryWrite(_entryStart);
        Complete();
    }

    internal void Abort()
    {
        var target = GetTarget();
        try
        {
            target.AbortEntryWrite(_entryStart);
        }
        finally
        {
            Complete();
        }
    }

    private JournalBatchWriterBase GetTarget()
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
        _target = null;
        _entryStart = 0;
        _completed = true;
    }
}
