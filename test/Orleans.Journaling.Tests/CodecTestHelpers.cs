using System.Buffers;
using System.Collections;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Buffers;
using Xunit;

namespace Orleans.Journaling.Tests;

public static class CodecTestHelpers
{
    public static ArrayBufferWriter<byte> Write(Action<IBufferWriter<byte>> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(buffer);
        return buffer;
    }

    public static ReadOnlySequence<byte> WriteEntry(Action<JournalStreamWriter> write)
    {
        using var batch = new OrleansBinaryJournalBatchWriter();
        write(batch.CreateJournalStreamWriter(new JournalStreamId(1)));
        using var committed = batch.PeekSlice();
        var sequence = committed.AsReadOnlySequence();

        // Strip the [varuint32 body length][varuint64 stream id] framing and return the operation payload.
        var lengthReader = Reader.Create(sequence, session: null!);
        var bodyLength = lengthReader.ReadVarUInt32();
        var entry = sequence.Slice(lengthReader.Position, bodyLength);
        var streamIdReader = Reader.Create(entry, session: null!);
        streamIdReader.ReadVarUInt64();
        var payload = entry.Slice(streamIdReader.Position);
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

    public static void AppendEntry(JournalStreamWriter writer, ReadOnlySpan<byte> payload)
    {
        using var entry = writer.BeginEntry();
        entry.Writer.Write(payload);
        entry.Commit();
    }

    public static void AssertOperationCodecRegistrations(IServiceProvider serviceProvider, string journalFormatKey)
    {
        AssertOperationCodec<IDictionaryOperationCodec<string, int>>(serviceProvider, journalFormatKey);
        AssertOperationCodec<IDictionaryOperationCodec<string, ulong>>(serviceProvider, journalFormatKey);
        AssertOperationCodec<IListOperationCodec<int>>(serviceProvider, journalFormatKey);
        AssertOperationCodec<IQueueOperationCodec<int>>(serviceProvider, journalFormatKey);
        AssertOperationCodec<ISetOperationCodec<int>>(serviceProvider, journalFormatKey);
        AssertOperationCodec<IValueOperationCodec<int>>(serviceProvider, journalFormatKey);
        AssertOperationCodec<IStateOperationCodec<int>>(serviceProvider, journalFormatKey);
        AssertOperationCodec<ITaskCompletionSourceOperationCodec<int>>(serviceProvider, journalFormatKey);
    }

    public static void AssertOperationCodecServiceRegistrations(IServiceProvider serviceProvider, string journalFormatKey)
    {
        AssertOperationCodecService<IDictionaryOperationCodec<string, int>>(serviceProvider, journalFormatKey);
        AssertOperationCodecService<IDictionaryOperationCodec<string, ulong>>(serviceProvider, journalFormatKey);
        AssertOperationCodecService<IListOperationCodec<int>>(serviceProvider, journalFormatKey);
        AssertOperationCodecService<IQueueOperationCodec<int>>(serviceProvider, journalFormatKey);
        AssertOperationCodecService<ISetOperationCodec<int>>(serviceProvider, journalFormatKey);
        AssertOperationCodecService<IValueOperationCodec<int>>(serviceProvider, journalFormatKey);
        AssertOperationCodecService<IStateOperationCodec<int>>(serviceProvider, journalFormatKey);
        AssertOperationCodecService<ITaskCompletionSourceOperationCodec<int>>(serviceProvider, journalFormatKey);
    }

    private static void AssertOperationCodec<TCodec>(IServiceProvider serviceProvider, string journalFormatKey)
        where TCodec : class
    {
        var codec = serviceProvider.GetRequiredKeyedService<TCodec>(journalFormatKey);
        Assert.Same(codec, serviceProvider.GetRequiredKeyedService<TCodec>(journalFormatKey));
    }

    private static void AssertOperationCodecService<TCodec>(IServiceProvider serviceProvider, string journalFormatKey)
        where TCodec : class
    {
        var keyedServiceProvider = serviceProvider.GetRequiredService<IServiceProviderIsKeyedService>();
        Assert.True(keyedServiceProvider.IsKeyedService(typeof(TCodec), journalFormatKey));
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

public sealed class RecordingDictionaryOperationHandler<TKey, TValue> : IDictionaryOperationHandler<TKey, TValue>
    where TKey : notnull
{
    public List<string> Commands { get; } = [];

    public List<KeyValuePair<TKey, TValue>> SnapshotItems { get; } = [];

    public void ApplySet(TKey key, TValue value)
    {
        Commands.Add($"set:{key}:{value}");
        SnapshotItems.Add(new(key, value));
    }

    public void ApplyRemove(TKey key) => Commands.Add($"remove:{key}");

    public void ApplyClear() => Commands.Add("clear");

    public void Reset(int capacityHint)
    {
        Commands.Add($"reset:{capacityHint}");
        SnapshotItems.Clear();
    }
}

public sealed class RecordingListOperationHandler<T> : IListOperationHandler<T>
{
    public List<string> Commands { get; } = [];

    public void ApplyAdd(T item) => Commands.Add($"add:{item}");

    public void ApplySet(int index, T item) => Commands.Add($"set:{index}:{item}");

    public void ApplyInsert(int index, T item) => Commands.Add($"insert:{index}:{item}");

    public void ApplyRemoveAt(int index) => Commands.Add($"remove:{index}");

    public void ApplyClear() => Commands.Add("clear");

    public void Reset(int capacityHint) => Commands.Add($"reset:{capacityHint}");
}

public sealed class RecordingQueueOperationHandler<T> : IQueueOperationHandler<T>
{
    public List<string> Commands { get; } = [];

    public void ApplyEnqueue(T item) => Commands.Add($"enqueue:{item}");

    public void ApplyDequeue() => Commands.Add("dequeue");

    public void ApplyClear() => Commands.Add("clear");

    public void Reset(int capacityHint) => Commands.Add($"reset:{capacityHint}");
}

public sealed class RecordingSetOperationHandler<T> : ISetOperationHandler<T>
{
    public List<string> Commands { get; } = [];

    public void ApplyAdd(T item) => Commands.Add($"add:{item}");

    public void ApplyRemove(T item) => Commands.Add($"remove:{item}");

    public void ApplyClear() => Commands.Add("clear");

    public void Reset(int capacityHint) => Commands.Add($"reset:{capacityHint}");
}

public sealed class RecordingValueOperationHandler<T> : IValueOperationHandler<T>
{
    public T? Value { get; private set; }

    public void ApplySet(T value) => Value = value;
}

public sealed class RecordingStateOperationHandler<T> : IStateOperationHandler<T>
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

public sealed class RecordingTaskCompletionSourceOperationHandler<T> : ITaskCompletionSourceOperationHandler<T>
{
    public List<string> Commands { get; } = [];

    public void ApplyPending() => Commands.Add("pending");

    public void ApplyCompleted(T value) => Commands.Add($"completed:{value}");

    public void ApplyFaulted(Exception exception) => Commands.Add($"faulted:{exception.Message}");

    public void ApplyCanceled() => Commands.Add("canceled");
}
