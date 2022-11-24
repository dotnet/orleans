using BenchmarkDotNet.Attributes;
using Benchmarks.Utilities;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;
using Orleans.Serialization.WireProtocol;
using Microsoft.Extensions.DependencyInjection;

namespace Benchmarks
{
    [Config(typeof(BenchmarkConfig))]
    public class FieldHeaderBenchmarks
    {
        private static readonly SerializerSession Session;
        private static readonly byte[] OrleansBuffer = new byte[1000];

        static FieldHeaderBenchmarks()
        {
            var services = new ServiceCollection().AddSerializer();
            var serviceProvider = services.BuildServiceProvider();
            var sessionPool = serviceProvider.GetRequiredService<SerializerSessionPool>();
            Session = sessionPool.GetSession();
        }

        [Benchmark(Baseline = true)]
        public void WritePlainExpectedEmbeddedId()
        {
            var writer = new SingleSegmentBuffer(OrleansBuffer).CreateWriter(Session);

            // Use an expected type and a field id with a value small enough to be embedded.
            writer.WriteFieldHeader(4, typeof(uint), typeof(uint), WireType.VarInt);
        }

        [Benchmark]
        public void WritePlainExpectedExtendedId()
        {
            var writer = new SingleSegmentBuffer(OrleansBuffer).CreateWriter(Session);

            // Use a field id delta which is too large to be embedded.
            writer.WriteFieldHeader(Tag.MaxEmbeddedFieldIdDelta + 20, typeof(uint), typeof(uint), WireType.VarInt);
        }

        [Benchmark]
        public void WriteFastEmbedded()
        {
            var writer = new SingleSegmentBuffer(OrleansBuffer).CreateWriter(Session);

            // Use an expected type and a field id with a value small enough to be embedded.
            writer.WriteFieldHeaderExpected(4, WireType.VarInt);
        }

        [Benchmark]
        public void WriteFastExtended()
        {
            var writer = new SingleSegmentBuffer(OrleansBuffer).CreateWriter(Session);

            // Use a field id delta which is too large to be embedded.
            writer.WriteFieldHeaderExpected(Tag.MaxEmbeddedFieldIdDelta + 20, WireType.VarInt);
        }

        [Benchmark]
        public void CreateWriter() => _ = new SingleSegmentBuffer(OrleansBuffer).CreateWriter(Session);

        [Benchmark]
        public void WriteByte()
        {
            var writer = new SingleSegmentBuffer(OrleansBuffer).CreateWriter(Session);
            writer.WriteByte((byte)4);
        }
    }
}