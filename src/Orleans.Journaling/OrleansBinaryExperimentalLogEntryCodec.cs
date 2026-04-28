using System.Buffers;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling;

internal sealed class OrleansBinaryExperimentalLogEntryCodec(IServiceProvider serviceProvider) : ILogEntryCodec
{
    private const byte FormatVersion = 0;

    public void WriteEntry<TEntry>(TEntry entry, IBufferWriter<byte> output)
        where TEntry : ILogEntry<TEntry>
    {
        var writer = new BinaryEntryWriter(serviceProvider, output);
        writer.WriteCommand(TEntry.Tag, TEntry.Name);
        TEntry.Write(ref writer, entry);
        writer.Complete();
    }

    public LogEntryCommand ReadCommand(ReadOnlySequence<byte> input)
    {
        var reader = new SequenceReader<byte>(input);
        ReadVersionByte(ref reader);
        return new(VarIntHelper.ReadVarUInt32(ref reader), null);
    }

    public void ApplyEntry<TEntry, TConsumer>(ReadOnlySequence<byte> input, TConsumer consumer)
        where TEntry : ILogEntry<TEntry, TConsumer>
    {
        var reader = new BinaryEntryReader(serviceProvider, input);
        TEntry.Apply(ref reader, consumer);
    }

    private static void WriteVersionByte(IBufferWriter<byte> output)
    {
        var span = output.GetSpan(1);
        span[0] = FormatVersion;
        output.Advance(1);
    }

    private static void ReadVersionByte(ref SequenceReader<byte> reader)
    {
        if (!reader.TryRead(out var version) || version != FormatVersion)
        {
            throw new NotSupportedException($"Unsupported format version: {version}");
        }
    }

    private static ILogDataCodec<T> GetCodec<T>(IServiceProvider serviceProvider) => serviceProvider.GetRequiredService<ILogDataCodec<T>>();

    private readonly struct BinaryEntryWriter(IServiceProvider serviceProvider, IBufferWriter<byte> output) : ILogEntryWriter
    {
        public void WriteCommand(uint tag, string name)
        {
            WriteVersionByte(output);
            VarIntHelper.WriteVarUInt32(output, tag);
        }

        public void WriteField<T>(uint tag, string name, T value) => GetCodec<T>(serviceProvider).Write(value, output);

        public void WriteRepeated<T>(uint tag, string name, uint countTag, string countName, IEnumerable<T> values, int count)
        {
            VarIntHelper.WriteVarUInt32(output, (uint)count);
            foreach (var value in values)
            {
                WriteField(tag, name, value);
            }
        }

        public void WriteKeyValuePairs<TKey, TValue>(
            uint tag,
            string name,
            uint countTag,
            string countName,
            uint keyTag,
            string keyName,
            uint valueTag,
            string valueName,
            IEnumerable<KeyValuePair<TKey, TValue>> values,
            int count)
        {
            VarIntHelper.WriteVarUInt32(output, (uint)count);
            foreach (var (key, value) in values)
            {
                WriteField(keyTag, keyName, key);
                WriteField(valueTag, valueName, value);
            }
        }

        public void Complete()
        {
        }
    }

    private struct BinaryEntryReader(IServiceProvider serviceProvider, ReadOnlySequence<byte> input) : ILogEntryReader
    {
        private ReadOnlySequence<byte> _remaining = GetPayload(input);

        public T ReadField<T>(uint tag, string name)
        {
            var result = GetCodec<T>(serviceProvider).Read(_remaining, out var bytesConsumed);
            _remaining = _remaining.Slice(bytesConsumed);
            return result;
        }

        public void ReadRepeated<T>(uint tag, string name, uint countTag, string countName, Action<int> start, Action<T> item)
        {
            var count = ReadCount();
            start(count);
            for (var i = 0; i < count; i++)
            {
                item(ReadField<T>(tag, name));
            }
        }

        public void ReadKeyValuePairs<TKey, TValue>(
            uint tag,
            string name,
            uint countTag,
            string countName,
            uint keyTag,
            string keyName,
            uint valueTag,
            string valueName,
            Action<int> start,
            Action<TKey, TValue> item)
        {
            var count = ReadCount();
            start(count);
            for (var i = 0; i < count; i++)
            {
                var key = ReadField<TKey>(keyTag, keyName);
                var value = ReadField<TValue>(valueTag, valueName);
                item(key, value);
            }
        }

        private int ReadCount()
        {
            var reader = new SequenceReader<byte>(_remaining);
            var result = (int)VarIntHelper.ReadVarUInt32(ref reader);
            _remaining = _remaining.Slice(reader.Consumed);
            return result;
        }

        private static ReadOnlySequence<byte> GetPayload(ReadOnlySequence<byte> input)
        {
            var reader = new SequenceReader<byte>(input);
            ReadVersionByte(ref reader);
            _ = VarIntHelper.ReadVarUInt32(ref reader);
            return input.Slice(reader.Consumed);
        }
    }
}
