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

    public static byte[] WriteEntry(Action<JournalStreamWriter> write)
    {
        using var batch = new OrleansBinaryJournalBufferWriter();
        write(batch.CreateJournalStreamWriter(new JournalStreamId(1)));
        using var committed = batch.GetBuffer();

        // Strip the OrleansBinary entry framing and return the operation payload.
        if (!OrleansBinaryJournalReader.TryReadVersionAndLength(committed, out var version, out var bodyLength, out var lengthPrefixLength))
        {
            throw new InvalidOperationException("The binary journal entry stream is malformed.");
        }

        var entry = committed.UnsafeSlice(lengthPrefixLength, checked((int)bodyLength));
        if (version == OrleansBinaryJournalReader.FramingVersion)
        {
            return entry.UnsafeSlice(sizeof(uint), entry.Length - sizeof(uint)).ToArray();
        }

        var streamIdReader = Reader.Create(entry, session: null!);
        streamIdReader.ReadVarUInt64();
        var payloadOffset = checked((int)streamIdReader.Position);
        return entry.UnsafeSlice(payloadOffset, entry.Length - payloadOffset).ToArray();
    }

    public static JournalBufferReader ReadBuffer(ReadOnlyMemory<byte> bytes)
    {
        var writer = new ArcBufferWriter();
        writer.Write(bytes.Span);
        return new JournalBufferReader(writer.Reader, isCompleted: true);
    }

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

    public static void AssertCommandCodecRegistrations(IServiceProvider serviceProvider, string journalFormatKey)
    {
        AssertCommandCodec<IDurableDictionaryCommandCodec<string, int>>(serviceProvider, journalFormatKey);
        AssertCommandCodec<IDurableDictionaryCommandCodec<string, uint>>(serviceProvider, journalFormatKey);
        AssertCommandCodec<IDurableListCommandCodec<int>>(serviceProvider, journalFormatKey);
        AssertCommandCodec<IDurableQueueCommandCodec<int>>(serviceProvider, journalFormatKey);
        AssertCommandCodec<IDurableSetCommandCodec<int>>(serviceProvider, journalFormatKey);
        AssertCommandCodec<IDurableValueCommandCodec<int>>(serviceProvider, journalFormatKey);
        AssertCommandCodec<IPersistentStateCommandCodec<int>>(serviceProvider, journalFormatKey);
        AssertCommandCodec<IDurableTaskCompletionSourceCommandCodec<int>>(serviceProvider, journalFormatKey);
    }

    public static void AssertCommandCodecServiceRegistrations(IServiceProvider serviceProvider, string journalFormatKey)
    {
        AssertCommandCodecService<IDurableDictionaryCommandCodec<string, int>>(serviceProvider, journalFormatKey);
        AssertCommandCodecService<IDurableDictionaryCommandCodec<string, uint>>(serviceProvider, journalFormatKey);
        AssertCommandCodecService<IDurableListCommandCodec<int>>(serviceProvider, journalFormatKey);
        AssertCommandCodecService<IDurableQueueCommandCodec<int>>(serviceProvider, journalFormatKey);
        AssertCommandCodecService<IDurableSetCommandCodec<int>>(serviceProvider, journalFormatKey);
        AssertCommandCodecService<IDurableValueCommandCodec<int>>(serviceProvider, journalFormatKey);
        AssertCommandCodecService<IPersistentStateCommandCodec<int>>(serviceProvider, journalFormatKey);
        AssertCommandCodecService<IDurableTaskCompletionSourceCommandCodec<int>>(serviceProvider, journalFormatKey);
    }

    private static void AssertCommandCodec<TCodec>(IServiceProvider serviceProvider, string journalFormatKey)
        where TCodec : class
    {
        var codec = serviceProvider.GetRequiredKeyedService<TCodec>(journalFormatKey);
        Assert.Same(codec, serviceProvider.GetRequiredKeyedService<TCodec>(journalFormatKey));
    }

    private static void AssertCommandCodecService<TCodec>(IServiceProvider serviceProvider, string journalFormatKey)
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

public sealed class RecordingDictionaryCommandHandler<TKey, TValue> : IDurableDictionaryCommandHandler<TKey, TValue>
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

public sealed class RecordingListCommandHandler<T> : IDurableListCommandHandler<T>
{
    public List<string> Commands { get; } = [];

    public void ApplyAdd(T item) => Commands.Add($"add:{item}");

    public void ApplySet(int index, T item) => Commands.Add($"set:{index}:{item}");

    public void ApplyInsert(int index, T item) => Commands.Add($"insert:{index}:{item}");

    public void ApplyRemoveAt(int index) => Commands.Add($"remove:{index}");

    public void ApplyClear() => Commands.Add("clear");

    public void Reset(int capacityHint) => Commands.Add($"reset:{capacityHint}");
}

public sealed class RecordingQueueCommandHandler<T> : IDurableQueueCommandHandler<T>
{
    public List<string> Commands { get; } = [];

    public void ApplyEnqueue(T item) => Commands.Add($"enqueue:{item}");

    public void ApplyDequeue() => Commands.Add("dequeue");

    public void ApplyClear() => Commands.Add("clear");

    public void Reset(int capacityHint) => Commands.Add($"reset:{capacityHint}");
}

public sealed class RecordingSetCommandHandler<T> : IDurableSetCommandHandler<T>
{
    public List<string> Commands { get; } = [];

    public void ApplyAdd(T item) => Commands.Add($"add:{item}");

    public void ApplyRemove(T item) => Commands.Add($"remove:{item}");

    public void ApplyClear() => Commands.Add("clear");

    public void Reset(int capacityHint) => Commands.Add($"reset:{capacityHint}");
}

public sealed class RecordingValueCommandHandler<T> : IDurableValueCommandHandler<T>
{
    public T? Value { get; private set; }

    public void ApplySet(T value) => Value = value;
}

public sealed class RecordingPersistentStateCommandHandler<T> : IPersistentStateCommandHandler<T>
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

public sealed class RecordingTaskCompletionSourceCommandHandler<T> : IDurableTaskCompletionSourceCommandHandler<T>
{
    public List<string> Commands { get; } = [];

    public void ApplyPending() => Commands.Add("pending");

    public void ApplyCompleted(T value) => Commands.Add($"completed:{value}");

    public void ApplyFaulted(Exception exception) => Commands.Add($"faulted:{exception.Message}");

    public void ApplyCanceled() => Commands.Add("canceled");
}
