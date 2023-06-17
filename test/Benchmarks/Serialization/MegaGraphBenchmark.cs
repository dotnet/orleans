using BenchmarkDotNet.Attributes;
using Benchmarks.Utilities;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Buffers.Adaptors;
using Orleans.Serialization.Session;
using Microsoft.Extensions.DependencyInjection;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Pipelines;
using System.Linq;
using Xunit;
using SerializerSession = Orleans.Serialization.Session.SerializerSession;

namespace Benchmarks
{
    [Trait("Category", "Benchmark")]
    [Config(typeof(BenchmarkConfig))]
    public class MegaGraphBenchmark
    {
        private static readonly Serializer<Dictionary<string, int>> Serializer;
        private static readonly byte[] Input;
        private static readonly SerializerSession Session;
        private static readonly Dictionary<string, int> Value;

        static MegaGraphBenchmark()
        {
            const int Size = 250_000;
            Value = new Dictionary<string, int>(Size);
            for (var i = 0; i < Size; i++)
            {
                Value[i.ToString(CultureInfo.InvariantCulture)] = i;
            }
            
            var services = new ServiceCollection()
                .AddSerializer()
                .BuildServiceProvider();
            Serializer = services.GetRequiredService<Serializer<Dictionary<string, int>>>();
            Session = services.GetRequiredService<SerializerSessionPool>().GetSession();
            var pipe = new Pipe(new PipeOptions(readerScheduler: PipeScheduler.Inline, writerScheduler: PipeScheduler.Inline));
            var writer = pipe.Writer.CreateWriter(Session);
            Serializer.Serialize(Value, ref writer);
            pipe.Writer.FlushAsync();
            pipe.Reader.TryRead(out var result);
            Input = result.Buffer.ToArray();
        }

        [Fact]
        [Benchmark]
        public object Deserialize()
        {
            Session.Reset();
            var instance = Serializer.Deserialize(Input, Session);
            return instance;
        }

        [Fact]
        [Benchmark]
        public int Serialize()
        {
            Session.Reset();
            var writer = Writer.CreatePooled(Session);
            Serializer.Serialize(Value, ref writer);
            writer.Output.Dispose();
            return writer.Position;
        }
    }
}