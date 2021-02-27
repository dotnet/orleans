using BenchmarkDotNet.Attributes;
using Benchmarks.Utilities;
using FakeFx.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Session;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Net;
using Xunit;
using SerializerSession = Orleans.Serialization.Session.SerializerSession;

namespace Benchmarks
{
    [Trait("Category", "Benchmark")]
    [Config(typeof(BenchmarkConfig))]
    public class MessageBenchmark
    {
        private static readonly Serializer<Message.HeadersContainer> Serializer;
        private static readonly byte[] Input;
        private static readonly SerializerSession Session;
        private static readonly Message.HeadersContainer Value;

        static MessageBenchmark()
        {
            var body = new Response("yess!");
            Value = (new Message
            {
                TargetActivation = ActivationId.NewId(),
                TargetSilo = SiloAddress.New(IPEndPoint.Parse("210.50.4.44:40902"), 5423123),
                TargetGrain = GrainId.Create("sys.mygrain", "borken_thee_doggo"),
                BodyObject = body,
                InterfaceType = GrainInterfaceType.Create("imygrain"),
                SendingActivation = ActivationId.NewId(),
                SendingSilo = SiloAddress.New(IPEndPoint.Parse("10.50.4.44:40902"), 5423123),
                SendingGrain = GrainId.Create("sys.mygrain", "fluffy_g"),
                TraceContext = new TraceContext { ActivityId = Guid.NewGuid() },
                Id = CorrelationId.GetNext()
            }).Headers;

            // 
            var services = new ServiceCollection()
                .AddSerializer()
                .BuildServiceProvider();
            Serializer = services.GetRequiredService<Serializer<Message.HeadersContainer>>();
            var bytes = new byte[4000];
            Session = services.GetRequiredService<SerializerSessionPool>().GetSession();
            var writer = new SingleSegmentBuffer(bytes).CreateWriter(Session);
            Serializer.Serialize(Value, ref writer);
            Input = bytes;
        }

        [Fact]
        [Benchmark]
        public object Deserialize()
        {
            Session.FullReset();
            var instance = Serializer.Deserialize(Input, Session);
            return instance;
        }

        [Fact]
        [Benchmark]
        public int Serialize()
        {
            Session.FullReset();
            return Serializer.Serialize(Value, Input, Session);
        }
    }
}