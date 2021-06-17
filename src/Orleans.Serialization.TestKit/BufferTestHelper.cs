using Orleans.Serialization.Buffers;
using Orleans.Serialization.Session;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Orleans.Serialization.TestKit
{
    [ExcludeFromCodeCoverage]
    public static class BufferTestHelper<TValue>
    {
        public static IBufferTestSerializer[] GetTestSerializers(IServiceProvider serviceProvider, int[] maxSizes)
        {
            var result = new List<IBufferTestSerializer>();
            foreach (var size in maxSizes)
            {
                result.Add(ActivatorUtilities.CreateInstance<MultiSegmentBufferWriterTester>(serviceProvider, new MultiSegmentBufferWriterTester.Options { MaxAllocationSize = size }));
            }

            result.Add(ActivatorUtilities.CreateInstance<StructBufferWriterTester>(serviceProvider));
            return result.ToArray();
        }

        public interface IBufferTestSerializer
        {
            IOutputBuffer Serialize(TValue input);
            void Deserialize(ReadOnlySequence<byte> buffer, out TValue output);
        }

        [ExcludeFromCodeCoverage]
        private abstract class BufferTester<TBufferWriter> : IBufferTestSerializer where TBufferWriter : IBufferWriter<byte>, IOutputBuffer
        {
            private readonly SerializerSessionPool _sessionPool;
            private readonly Serializer<TValue> _serializer;

            protected BufferTester(IServiceProvider serviceProvider)
            {
                _sessionPool = serviceProvider.GetRequiredService<SerializerSessionPool>();
                _serializer = serviceProvider.GetRequiredService<Serializer<TValue>>();
            }

            protected abstract TBufferWriter CreateBufferWriter();

            public IOutputBuffer Serialize(TValue input)
            {
                using var session = _sessionPool.GetSession();
                var writer = Writer.Create(CreateBufferWriter(), session);
                _serializer.Serialize(input, ref writer);
                return writer.Output;
            }

            public void Deserialize(ReadOnlySequence<byte> buffer, out TValue output)
            {
                using var session = _sessionPool.GetSession();
                var reader = Reader.Create(buffer, session);
                output = _serializer.Deserialize(ref reader);
            }
        }

        [ExcludeFromCodeCoverage]
        private class MultiSegmentBufferWriterTester : BufferTester<TestMultiSegmentBufferWriter>
        {
            private readonly Options _options;

            public MultiSegmentBufferWriterTester(IServiceProvider serviceProvider, Options options) : base(serviceProvider)
            {
                _options = options;
            }

            public class Options
            {
                public int MaxAllocationSize { get; set; }
            }

            protected override TestMultiSegmentBufferWriter CreateBufferWriter() => new(_options.MaxAllocationSize);

            public override string ToString() => $"{nameof(TestMultiSegmentBufferWriter)} {nameof(_options.MaxAllocationSize)}: {_options.MaxAllocationSize}";
        }

        [ExcludeFromCodeCoverage]
        private class StructBufferWriterTester : BufferTester<TestBufferWriterStruct>
        {
            protected override TestBufferWriterStruct CreateBufferWriter() => new(new byte[102400]);

            public StructBufferWriterTester(IServiceProvider serviceProvider) : base(serviceProvider)
            {
            }

            public override string ToString() => $"{nameof(TestBufferWriterStruct)}";
        }
    }
}