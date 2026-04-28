using System.Buffers;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling.Protobuf;

internal sealed class ProtobufExperimentalLogEntryCodec(IServiceProvider serviceProvider) : ILogEntryCodec
{
    private const uint CommandField = 1;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly ConcurrentDictionary<Type, object> _converters = new();

    public void WriteEntry<TEntry>(TEntry entry, IBufferWriter<byte> output)
        where TEntry : ILogEntry<TEntry>
    {
        var writer = new ProtobufEntryWriter(this, output);
        writer.WriteCommand(TEntry.Tag, TEntry.Name);
        TEntry.Write(ref writer, entry);
        writer.Complete();
    }

    public LogEntryCommand ReadCommand(ReadOnlySequence<byte> input)
    {
        var reader = new SequenceReader<byte>(input);
        var hasCommand = false;
        var command = uint.MaxValue;
        while (!reader.End)
        {
            var tag = ProtobufWire.ReadTag(ref reader);
            var field = tag >> 3;
            if (field == CommandField)
            {
                ProtobufWire.RequireNoDuplicateCommand(hasCommand);
                command = ProtobufWire.ReadUInt32(ref reader);
                hasCommand = true;
            }
            else
            {
                ProtobufWire.SkipField(ref reader, tag);
            }
        }

        ProtobufWire.RequireCommand(hasCommand);
        return new(command, null);
    }

    public void ApplyEntry<TEntry, TConsumer>(ReadOnlySequence<byte> input, TConsumer consumer)
        where TEntry : ILogEntry<TEntry, TConsumer>
    {
        var reader = new ProtobufEntryReader(this, input);
        TEntry.Apply(ref reader, consumer);
    }

    private ProtobufValueConverter<T> GetConverter<T>()
        => (ProtobufValueConverter<T>)_converters.GetOrAdd(
            typeof(T),
            static (_, state) =>
            {
                var self = (ProtobufExperimentalLogEntryCodec)state!;
                return ProtobufValueConverter<T>.IsNativeType
                    ? new ProtobufValueConverter<T>()
                    : new ProtobufValueConverter<T>(self._serviceProvider.GetRequiredService<ILogDataCodec<T>>());
            },
            this);

    private readonly struct ProtobufEntryWriter(ProtobufExperimentalLogEntryCodec codec, IBufferWriter<byte> output) : ILogEntryWriter
    {
        public void WriteCommand(uint tag, string name) => ProtobufWire.WriteUInt32Field(output, CommandField, tag);

        public void WriteField<T>(uint tag, string name, T value)
            => ProtobufWire.WriteBytesField(output, tag, codec.GetConverter<T>().ToBytes(value));

        public void WriteRepeated<T>(uint tag, string name, uint countTag, string countName, IEnumerable<T> values, int count)
        {
            ProtobufWire.WriteUInt32Field(output, countTag, (uint)count);
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
            ProtobufWire.WriteUInt32Field(output, countTag, (uint)count);
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

    private readonly struct ProtobufEntryReader(ProtobufExperimentalLogEntryCodec codec, ReadOnlySequence<byte> input) : ILogEntryReader
    {
        public T ReadField<T>(uint tag, string name)
        {
            var reader = new SequenceReader<byte>(input);
            var command = uint.MaxValue;
            var hasCommand = false;
            var hasField = false;
            T? value = default;
            while (!reader.End)
            {
                var wireTag = ProtobufWire.ReadTag(ref reader);
                var field = wireTag >> 3;
                if (field == CommandField)
                {
                    ProtobufWire.RequireNoDuplicateCommand(hasCommand);
                    command = ProtobufWire.ReadUInt32(ref reader);
                    hasCommand = true;
                }
                else if (field == tag)
                {
                    ProtobufWire.RequireCommand(hasCommand);
                    if (hasField)
                    {
                        throw new InvalidOperationException($"Malformed protobuf log entry: duplicate field '{name}'.");
                    }

                    value = codec.GetConverter<T>().FromBytes(ProtobufWire.ReadBytes(ref reader));
                    hasField = true;
                }
                else
                {
                    ProtobufWire.SkipField(ref reader, wireTag);
                }
            }

            ProtobufWire.RequireCommand(hasCommand);
            return ProtobufWire.RequireValue(hasField, value, name, command);
        }

        public void ReadRepeated<T>(uint tag, string name, uint countTag, string countName, Action<int> start, Action<T> item)
        {
            var reader = new SequenceReader<byte>(input);
            var command = uint.MaxValue;
            var count = 0;
            var hasCommand = false;
            var hasCount = false;
            var started = false;
            var actualCount = 0;
            while (!reader.End)
            {
                var wireTag = ProtobufWire.ReadTag(ref reader);
                var field = wireTag >> 3;
                if (field == CommandField)
                {
                    ProtobufWire.RequireNoDuplicateCommand(hasCommand);
                    command = ProtobufWire.ReadUInt32(ref reader);
                    hasCommand = true;
                }
                else if (field == countTag)
                {
                    ProtobufWire.RequireCommand(hasCommand);
                    if (hasCount)
                    {
                        throw new InvalidOperationException($"Malformed protobuf log entry: duplicate field '{countName}'.");
                    }

                    count = (int)ProtobufWire.ReadUInt32(ref reader);
                    hasCount = true;
                }
                else if (field == tag)
                {
                    ProtobufWire.RequireCommand(hasCommand);
                    if (!started)
                    {
                        ProtobufWire.RequireField(hasCount, countName, command);
                        start(count);
                        started = true;
                    }

                    item(codec.GetConverter<T>().FromBytes(ProtobufWire.ReadBytes(ref reader)));
                    actualCount++;
                }
                else
                {
                    ProtobufWire.SkipField(ref reader, wireTag);
                }
            }

            ProtobufWire.RequireCommand(hasCommand);
            ProtobufWire.RequireField(hasCount, countName, command);
            ProtobufWire.RequireSnapshotCount(count, actualCount, command);
            if (!started)
            {
                start(count);
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
            var reader = new SequenceReader<byte>(input);
            var command = uint.MaxValue;
            var count = 0;
            var hasCommand = false;
            var hasCount = false;
            var hasKey = false;
            var started = false;
            var actualCount = 0;
            TKey? key = default;
            while (!reader.End)
            {
                var wireTag = ProtobufWire.ReadTag(ref reader);
                var field = wireTag >> 3;
                if (field == CommandField)
                {
                    ProtobufWire.RequireNoDuplicateCommand(hasCommand);
                    command = ProtobufWire.ReadUInt32(ref reader);
                    hasCommand = true;
                }
                else if (field == countTag)
                {
                    ProtobufWire.RequireCommand(hasCommand);
                    if (hasCount)
                    {
                        throw new InvalidOperationException($"Malformed protobuf log entry: duplicate field '{countName}'.");
                    }

                    count = (int)ProtobufWire.ReadUInt32(ref reader);
                    hasCount = true;
                }
                else if (field == keyTag)
                {
                    ProtobufWire.RequireCommand(hasCommand);
                    if (hasKey)
                    {
                        ProtobufWire.RequireField(false, valueName, command);
                    }

                    if (!started)
                    {
                        ProtobufWire.RequireField(hasCount, countName, command);
                        start(count);
                        started = true;
                    }

                    key = codec.GetConverter<TKey>().FromBytes(ProtobufWire.ReadBytes(ref reader));
                    hasKey = true;
                }
                else if (field == valueTag)
                {
                    ProtobufWire.RequireCommand(hasCommand);
                    if (!started)
                    {
                        ProtobufWire.RequireField(hasCount, countName, command);
                        start(count);
                        started = true;
                    }

                    var value = codec.GetConverter<TValue>().FromBytes(ProtobufWire.ReadBytes(ref reader));
                    item(ProtobufWire.RequireValue(hasKey, key, keyName, command), value);
                    key = default;
                    hasKey = false;
                    actualCount++;
                }
                else
                {
                    ProtobufWire.SkipField(ref reader, wireTag);
                }
            }

            ProtobufWire.RequireCommand(hasCommand);
            ProtobufWire.RequireField(hasCount, countName, command);
            ProtobufWire.RequireField(!hasKey, valueName, command);
            ProtobufWire.RequireSnapshotCount(count, actualCount, command);
            if (!started)
            {
                start(count);
            }
        }
    }
}
