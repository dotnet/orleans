using BenchmarkDotNet.Attributes;
using Benchmarks.Models;
using Benchmarks.Utilities;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Session;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Xunit;
using SerializerSession = Orleans.Serialization.Session.SerializerSession;
using Utf8JsonNS = Utf8Json;
using Hyperion;
using System.Buffers;

namespace Benchmarks.Comparison
{
    [Trait("Category", "Benchmark")]
    [Config(typeof(BenchmarkConfig))]
    //[DisassemblyDiagnoser(recursiveDepth: 2, printSource: true)]
    //[EtwProfiler]
    public class ClassDeserializeBenchmark
    {
        private static readonly MemoryStream ProtoInput;

        private static readonly ReadOnlySequence<byte> GoogleProtoInput;

        private static readonly byte[] MsgPackInput = MessagePack.MessagePackSerializer.Serialize(IntClass.Create());

        private static readonly string NewtonsoftJsonInput = JsonConvert.SerializeObject(IntClass.Create());

        private static readonly byte[] SpanJsonInput = SpanJson.JsonSerializer.Generic.Utf8.Serialize(IntClass.Create());

        private static readonly Hyperion.Serializer HyperionSerializer = new(SerializerOptions.Default.WithKnownTypes(new[] { typeof(IntClass) }));
        private static readonly MemoryStream HyperionInput;

        private static readonly Serializer<IntClass> Serializer;
        private static readonly byte[] Input;
        private static readonly SerializerSession Session;

        private static readonly DeserializerSession HyperionSession;

        private static readonly Utf8JsonNS.IJsonFormatterResolver Utf8JsonResolver = Utf8JsonNS.Resolvers.StandardResolver.Default;
        private static readonly byte[] Utf8JsonInput;

        private static readonly byte[] SystemTextJsonInput;

        static ClassDeserializeBenchmark()
        {
            ProtoInput = new MemoryStream();
            ProtoBuf.Serializer.Serialize(ProtoInput, IntClass.Create());

            ProtoInput = new MemoryStream();
            GoogleProtoInput = new ReadOnlySequence<byte>(Google.Protobuf.MessageExtensions.ToByteArray(ProtoIntClass.Create()));

            HyperionInput = new MemoryStream();
            HyperionSession = HyperionSerializer.GetDeserializerSession();
            HyperionSerializer.Serialize(IntClass.Create(), HyperionInput);

            // 
            var services = new ServiceCollection()
                .AddSerializer()
                .BuildServiceProvider();
            Serializer = services.GetRequiredService<Serializer<IntClass>>();
            var bytes = new byte[1000];
            Session = services.GetRequiredService<SerializerSessionPool>().GetSession();
            var writer = new SingleSegmentBuffer(bytes).CreateWriter(Session);
            Serializer.Serialize(IntClass.Create(), ref writer);
            Input = bytes;

            Utf8JsonInput = Utf8JsonNS.JsonSerializer.Serialize(IntClass.Create(), Utf8JsonResolver);

            var stream = new MemoryStream();
            using (var jsonWriter = new System.Text.Json.Utf8JsonWriter(stream))
            {
                System.Text.Json.JsonSerializer.Serialize(jsonWriter, IntClass.Create());
            }

            SystemTextJsonInput = stream.ToArray();
        }

        private static int SumResult(IntClass result) => result.MyProperty1 +
                   result.MyProperty2 +
                   result.MyProperty3 +
                   result.MyProperty4 +
                   result.MyProperty5 +
                   result.MyProperty6 +
                   result.MyProperty7 +
                   result.MyProperty8 +
                   result.MyProperty9;

        private static int SumResult(ProtoIntClass result) => result.MyProperty1 +
                   result.MyProperty2 +
                   result.MyProperty3 +
                   result.MyProperty4 +
                   result.MyProperty5 +
                   result.MyProperty6 +
                   result.MyProperty7 +
                   result.MyProperty8 +
                   result.MyProperty9;

        [Fact]
        [Benchmark(Baseline = true)]
        public int Orleans()
        {
            Session.Reset();
            var instance = Serializer.Deserialize(Input, Session);
            return SumResult(instance);
        }
        
        [Benchmark]
        public int Utf8Json() => SumResult(Utf8JsonNS.JsonSerializer.Deserialize<IntClass>(Utf8JsonInput, Utf8JsonResolver));

        [Benchmark]
        public int SystemTextJson() => SumResult(System.Text.Json.JsonSerializer.Deserialize<IntClass>(SystemTextJsonInput));

        [Benchmark]
        public int MessagePackCSharp() => SumResult(MessagePack.MessagePackSerializer.Deserialize<IntClass>(MsgPackInput));

        [Benchmark]
        public int ProtobufNet()
        {
            ProtoInput.Position = 0;
            return SumResult(ProtoBuf.Serializer.Deserialize<IntClass>(ProtoInput));
        }

        [Benchmark]
        public int GoogleProtobuf()
        {
            return SumResult(ProtoIntClass.Parser.ParseFrom(GoogleProtoInput));
        }

        [Benchmark]
        public int Hyperion()
        {
            HyperionInput.Position = 0;

            return SumResult(HyperionSerializer.Deserialize<IntClass>(HyperionInput, HyperionSession));
        }

        [Benchmark]
        public int NewtonsoftJson() => SumResult(JsonConvert.DeserializeObject<IntClass>(NewtonsoftJsonInput));

        [Benchmark(Description = "SpanJson")]
        public int SpanJsonUtf8() => SumResult(SpanJson.JsonSerializer.Generic.Utf8.Deserialize<IntClass>(SpanJsonInput));
    }
}