using System.Buffers;
using System.Collections;

namespace Orleans.Journaling.Tests;

public static class CodecTestHelpers
{
    public static ArrayBufferWriter<byte> Write(Action<IBufferWriter<byte>> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(buffer);
        return buffer;
    }

    public static ReadOnlySequence<byte> WriteEntry(Action<LogStreamWriter> write)
    {
        using var batch = new OrleansBinaryLogBatchWriter();
        write(batch.CreateLogStreamWriter(new LogStreamId(1)));
        using var committed = batch.PeekSlice();
        var remaining = committed.AsReadOnlySequence();
        if (!OrleansBinaryLogEntryFrameReader.TryReadEntry(
            ref remaining,
            offset: 0,
            isCompleted: true,
            out _,
            out var payload,
            out _,
            out _))
        {
            throw new InvalidOperationException("The test log entry was not fully written.");
        }

        return Sequence(payload.ToArray());
    }

    public static ReadOnlySequence<byte> Sequence(ReadOnlyMemory<byte> bytes) => new(bytes);

    public static ReadOnlySequence<byte> SegmentedSequence(params byte[][] segments)
    {
        if (segments.Length == 0)
        {
            return ReadOnlySequence<byte>.Empty;
        }

        BufferSegment? first = null;
        BufferSegment? last = null;
        foreach (var segment in segments)
        {
            if (last is null)
            {
                first = last = new BufferSegment(segment);
            }
            else
            {
                last = last.Append(segment);
            }
        }

        return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
    }

    public static void AppendEntry(LogStreamWriter writer, ReadOnlySpan<byte> payload)
    {
        using var entry = writer.BeginEntry();
        entry.Writer.Write(payload);
        entry.Commit();
    }

    private sealed class BufferSegment : ReadOnlySequenceSegment<byte>
    {
        public BufferSegment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public BufferSegment Append(ReadOnlyMemory<byte> memory)
        {
            var segment = new BufferSegment(memory)
            {
                RunningIndex = RunningIndex + Memory.Length
            };

            Next = segment;
            return segment;
        }
    }
}

public sealed class MiscountedReadOnlyCollection<T>(int count, IReadOnlyCollection<T> items) : IReadOnlyCollection<T>
{
    public int Count => count;

    public IEnumerator<T> GetEnumerator() => items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class RecordingDictionaryOperationHandler<TKey, TValue> : IDurableDictionaryOperationHandler<TKey, TValue>
    where TKey : notnull
{
    public List<string> Commands { get; } = [];

    public List<KeyValuePair<TKey, TValue>> SnapshotItems { get; } = [];

    public void ApplySet(TKey key, TValue value) => Commands.Add($"set:{key}:{value}");

    public void ApplyRemove(TKey key) => Commands.Add($"remove:{key}");

    public void ApplyClear() => Commands.Add("clear");

    public void ApplySnapshotStart(int count)
    {
        Commands.Add($"snapshot:{count}");
        SnapshotItems.Clear();
    }

    public void ApplySnapshotItem(TKey key, TValue value)
    {
        Commands.Add($"snapshot-item:{key}:{value}");
        SnapshotItems.Add(new(key, value));
    }
}

public sealed class RecordingListOperationHandler<T> : IDurableListOperationHandler<T>
{
    public List<string> Commands { get; } = [];

    public void ApplyAdd(T item) => Commands.Add($"add:{item}");

    public void ApplySet(int index, T item) => Commands.Add($"set:{index}:{item}");

    public void ApplyInsert(int index, T item) => Commands.Add($"insert:{index}:{item}");

    public void ApplyRemoveAt(int index) => Commands.Add($"remove:{index}");

    public void ApplyClear() => Commands.Add("clear");

    public void ApplySnapshotStart(int count) => Commands.Add($"snapshot:{count}");

    public void ApplySnapshotItem(T item) => Commands.Add($"snapshot-item:{item}");
}

public sealed class RecordingQueueOperationHandler<T> : IDurableQueueOperationHandler<T>
{
    public List<string> Commands { get; } = [];

    public void ApplyEnqueue(T item) => Commands.Add($"enqueue:{item}");

    public void ApplyDequeue() => Commands.Add("dequeue");

    public void ApplyClear() => Commands.Add("clear");

    public void ApplySnapshotStart(int count) => Commands.Add($"snapshot:{count}");

    public void ApplySnapshotItem(T item) => Commands.Add($"snapshot-item:{item}");
}

public sealed class RecordingSetOperationHandler<T> : IDurableSetOperationHandler<T>
{
    public List<string> Commands { get; } = [];

    public void ApplyAdd(T item) => Commands.Add($"add:{item}");

    public void ApplyRemove(T item) => Commands.Add($"remove:{item}");

    public void ApplyClear() => Commands.Add("clear");

    public void ApplySnapshotStart(int count) => Commands.Add($"snapshot:{count}");

    public void ApplySnapshotItem(T item) => Commands.Add($"snapshot-item:{item}");
}

public sealed class RecordingValueOperationHandler<T> : IDurableValueOperationHandler<T>
{
    public T? Value { get; private set; }

    public void ApplySet(T value) => Value = value;
}

public sealed class RecordingStateOperationHandler<T> : IDurableStateOperationHandler<T>
{
    public List<string> Commands { get; } = [];

    public T? State { get; private set; }

    public ulong Version { get; private set; }

    public void ApplySet(T state, ulong version)
    {
        State = state;
        Version = version;
        Commands.Add($"set:{state}:{version}");
    }

    public void ApplyClear() => Commands.Add("clear");
}

public sealed class RecordingTaskCompletionSourceOperationHandler<T> : IDurableTaskCompletionSourceOperationHandler<T>
{
    public List<string> Commands { get; } = [];

    public void ApplyPending() => Commands.Add("pending");

    public void ApplyCompleted(T value) => Commands.Add($"completed:{value}");

    public void ApplyFaulted(Exception exception) => Commands.Add($"faulted:{exception.Message}");

    public void ApplyCanceled() => Commands.Add("canceled");
}
