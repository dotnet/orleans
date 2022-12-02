using System.Buffers;
using System.IO.Pipelines;
using BenchmarkDotNet.Attributes;
using Benchmarks.Models;
using Benchmarks.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Networking.Shared;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Session;
using Xunit;

namespace Benchmarks
{
    [Trait("Category", "Benchmark")]
    [Config(typeof(BenchmarkConfig))]
    [MemoryDiagnoser]
    public class ComplexTypeBenchmarks
    {
        private static SingleSegmentBuffer Buffer = new(new byte[1000]);
        private readonly Serializer<SimpleStruct> _structSerializer;
        private readonly DeepCopier<SimpleStruct> _structCopier;
        private readonly Serializer<ComplexClass> _serializer;
        private readonly DeepCopier<ComplexClass> _copier;
        private readonly SerializerSessionPool _sessionPool;
        private readonly ComplexClass _value;
        private readonly SerializerSession _session;
        private readonly ReadOnlySequence<byte> _serializedPayload;
        private readonly long _readBytesLength;
        private readonly Pipe _pipe;
        private readonly MessageSerializer _messageSerializer;
        private readonly SimpleStruct _structValue;
        private readonly Message _message;
        private readonly Message _structMessage;

        public ComplexTypeBenchmarks()
        {
            var services = new ServiceCollection();
            _ = services
                .AddSerializer();
            var serviceProvider = services.BuildServiceProvider();
            _serializer = serviceProvider.GetRequiredService<Serializer<ComplexClass>>();
            _copier = serviceProvider.GetRequiredService<DeepCopier<ComplexClass>>();
            _structSerializer = serviceProvider.GetRequiredService<Serializer<SimpleStruct>>();
            _structCopier = serviceProvider.GetRequiredService<DeepCopier<SimpleStruct>>();
            _sessionPool = serviceProvider.GetRequiredService<SerializerSessionPool>();
            _value = new ComplexClass
            {
                BaseInt = 192,
                Int = 501,
                String = "bananas",
                //Array = Enumerable.Range(0, 60).ToArray(),
                //MultiDimensionalArray = new[,] {{0, 2, 4}, {1, 5, 6}}
            };
            _value.AlsoSelf = _value.BaseSelf = _value.Self = _value;
            _message = new() { BodyObject = new Response<ComplexClass> { TypedResult = _value } };

            _structValue = new SimpleStruct
            {
                Int = 42,
                Bool = true,
                Guid = Guid.NewGuid()
            };
            _structMessage = new() { BodyObject = new Response<SimpleStruct> { TypedResult = _structValue } };

            _session = _sessionPool.GetSession();
            var writer = Buffer.CreateWriter(_session);

            _serializer.Serialize(_value, ref writer);
            var bytes = new byte[writer.Output.GetMemory().Length];
            writer.Output.GetReadOnlySpan().CopyTo(bytes);
            _serializedPayload = new ReadOnlySequence<byte>(bytes);
            Buffer.Reset();
            _readBytesLength = _serializedPayload.Length;

            _pipe = new Pipe(new PipeOptions(readerScheduler: PipeScheduler.Inline, writerScheduler: PipeScheduler.Inline, pauseWriterThreshold: 0));
            _messageSerializer = new(_sessionPool, new SharedMemoryPool(), new SiloMessagingOptions());
        }

        [Fact]
        public void SerializeComplex()
        {
            var writer = Buffer.CreateWriter(_session);
            _session.FullReset();
            _serializer.Serialize(_value, ref writer);

            _session.FullReset();
            var reader = Reader.Create(writer.Output.GetReadOnlySpan(), _session);
            _ = _serializer.Deserialize(ref reader);
            Buffer.Reset();
        }

        [Fact]
        public void CopyComplex()
        {
            _copier.Copy(_value); 
        }

        [Fact]
        public void CopyComplexStruct()
        {
            _structCopier.Copy(_structValue); 
        }

        [Fact]
        [Benchmark]
        public SimpleStruct OrleansStructRoundTrip()
        {
            var writer = Buffer.CreateWriter(_session);
            _session.FullReset();
            _structSerializer.Serialize(_structValue, ref writer);

            _session.FullReset();
            var reader = Reader.Create(writer.Output.GetReadOnlySpan(), _session);
            var result = _structSerializer.Deserialize(ref reader);
            Buffer.Reset();
            return result;
        }

        [Fact]
        [Benchmark]
        public void OrleansMessageSerializerStructRoundTrip()
        {
            _messageSerializer.Write(_pipe.Writer, _structMessage);
            _pipe.Writer.FlushAsync().AsTask().GetAwaiter().GetResult();

            _pipe.Reader.TryRead(out var readResult);
            var reader = readResult.Buffer;
            _messageSerializer.TryRead(ref reader, out var result);

            ((Response<SimpleStruct>)result.BodyObject).Dispose();

            _pipe.Writer.Complete();
            _pipe.Reader.Complete();
            _pipe.Reset();
        }

        [Fact]
        //[Benchmark]
        public object OrleansClassRoundTrip()
        {
            var writer = Buffer.CreateWriter(_session);
            _session.FullReset();
            _serializer.Serialize(_value, ref writer);

            _session.FullReset();
            var reader = Reader.Create(writer.Output.GetReadOnlySpan(), _session);
            var result = _serializer.Deserialize(ref reader);
            Buffer.Reset();
            return result;
        }

        [Fact]
        //[Benchmark]
        public void OrleansMessageSerializerClassRoundTrip()
        {
            _messageSerializer.Write(_pipe.Writer, _message);
            _pipe.Writer.FlushAsync().AsTask().GetAwaiter().GetResult();

            _pipe.Reader.TryRead(out var readResult);
            var reader = readResult.Buffer;
            _messageSerializer.TryRead(ref reader, out var result);

            ((Response<ComplexClass>)result.BodyObject).Dispose();

            _pipe.Writer.Complete();
            _pipe.Reader.Complete();
            _pipe.Reset();
        }

        [Fact]
        //[Benchmark]
        public object OrleansSerialize()
        {
            var writer = Buffer.CreateWriter(_session);
            _session.FullReset();
            _serializer.Serialize(_value, ref writer);
            Buffer.Reset();
            return _session;
        }

        [Fact]
        //[Benchmark]
        public object OrleansDeserialize()
        {
            _session.FullReset();
            var reader = Reader.Create(_serializedPayload, _session);
            return _serializer.Deserialize(ref reader);
        }

        [Fact]
        //[Benchmark]
        public int OrleansReadEachByte()
        {
            var sum = 0;
            var reader = Reader.Create(_serializedPayload, _session);
            for (var i = 0; i < _readBytesLength; i++)
            {
                sum ^= reader.ReadByte();
            }

            return sum;
        }
    }
}