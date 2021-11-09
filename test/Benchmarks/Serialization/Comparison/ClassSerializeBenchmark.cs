using BenchmarkDotNet.Attributes;
using Benchmarks.Models;
using Benchmarks.Utilities;
using Orleans.Serialization;
using Orleans.Serialization.Session;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using System.IO;
using System.Text;
using System.Text.Json;
using Xunit;
using SerializerSession = Orleans.Serialization.Session.SerializerSession;
using Utf8JsonNS = Utf8Json;
using Hyperion;
using ZeroFormatter;
using System.Buffers;
using Google.Protobuf;

namespace Benchmarks.Comparison
{
    [Trait("Category", "Benchmark")]
    [Config(typeof(BenchmarkConfig))]
    [PayloadSizeColumn]
    public class ClassSerializeBenchmark
    {
        private static readonly IntClass Input = IntClass.Create();
        private static readonly VirtualIntsClass ZeroFormatterInput = VirtualIntsClass.Create();
        private static readonly IBufferMessage ProtoInput = ProtoIntClass.Create();

        private static readonly Hyperion.Serializer HyperionSerializer = new(SerializerOptions.Default.WithKnownTypes(new[] { typeof(IntClass) }));
        private static readonly Hyperion.SerializerSession HyperionSession;
        private static readonly MemoryStream HyperionBuffer = new();

        private static readonly Serializer<IntClass> Serializer;
        private static readonly byte[] Data;
        private static readonly SerializerSession Session;

        private static readonly MemoryStream ProtoBuffer = new();

        private static readonly ClassSingleSegmentBuffer ProtoSegmentBuffer;

        private static readonly MemoryStream Utf8JsonOutput = new();
        private static readonly Utf8JsonNS.IJsonFormatterResolver Utf8JsonResolver = Utf8JsonNS.Resolvers.StandardResolver.Default;

        private static readonly MemoryStream SystemTextJsonOutput = new();
        private static readonly Utf8JsonWriter SystemTextJsonWriter;

        static ClassSerializeBenchmark()
        {
            // 
            var services = new ServiceCollection()
                .AddSerializer()
                .BuildServiceProvider();
            Serializer = services.GetRequiredService<Serializer<IntClass>>();
            Session = services.GetRequiredService<SerializerSessionPool>().GetSession();
            Data = new byte[1000];

            HyperionSession = HyperionSerializer.GetSerializerSession();

            SystemTextJsonWriter = new Utf8JsonWriter(SystemTextJsonOutput);

            ProtoSegmentBuffer = new ClassSingleSegmentBuffer(Data);
        }

        [Fact]
        [Benchmark(Baseline = true)]
        public long Orleans()
        {
            Session.PartialReset();
            return Serializer.Serialize(Input, Data, Session);
        }

        [Benchmark]
        public long Utf8Json()
        {
            Utf8JsonOutput.Position = 0;
            Utf8JsonNS.JsonSerializer.Serialize(Utf8JsonOutput, Input, Utf8JsonResolver);
            return Utf8JsonOutput.Length;
        }

        [Benchmark]
        public long SystemTextJson()
        {
            SystemTextJsonOutput.Position = 0;
            System.Text.Json.JsonSerializer.Serialize(SystemTextJsonWriter, Input);
            SystemTextJsonWriter.Reset();
            return SystemTextJsonOutput.Length;
        }

        [Benchmark]
        public int MessagePackCSharp()
        {
            var bytes = MessagePack.MessagePackSerializer.Serialize(Input);
            return bytes.Length;
        }

        [Benchmark]
        public long ProtobufNet()
        {
            ProtoBuffer.Position = 0;
            ProtoBuf.Serializer.Serialize(ProtoBuffer, Input);
            return ProtoBuffer.Length;
        }

        [Benchmark]
        public long GoogleProtobuf()
        {
            ProtoSegmentBuffer.Reset();
            ProtoInput.WriteTo(ProtoSegmentBuffer);
            return ProtoSegmentBuffer.Length;
        }

        [Benchmark]
        public long Hyperion()
        {
            HyperionBuffer.Position = 0;
            HyperionSerializer.Serialize(Input, HyperionBuffer, HyperionSession);
            return HyperionBuffer.Length;
        }

        //[Benchmark]
        public int ZeroFormatter()
        {
            var bytes = ZeroFormatterSerializer.Serialize(ZeroFormatterInput);
            return bytes.Length;
        }

        [Benchmark]
        public int NewtonsoftJson()
        {
            var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(Input));
            return bytes.Length;
        }

        [Benchmark(Description = "SpanJson")]
        public int SpanJsonUtf8()
        {
            var bytes = SpanJson.JsonSerializer.Generic.Utf8.Serialize(Input);
            return bytes.Length;
        }
    }
}