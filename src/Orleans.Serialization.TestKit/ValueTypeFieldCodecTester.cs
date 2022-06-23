using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Microsoft.Extensions.DependencyInjection;
using System.IO.Pipelines;
using Xunit;
using Orleans.Serialization.Serializers;

namespace Orleans.Serialization.TestKit
{
    public abstract class ValueTypeFieldCodecTester<TField, TCodec> : FieldCodecTester<TField, TCodec> where TField : struct where TCodec : class, IFieldCodec<TField>
    {
        [Fact]
        public void ValueSerializerRoundTrip()
        {
            var serializer = ServiceProvider.GetRequiredService<ValueSerializer<TField>>();
            foreach (var value in TestValues)
            {
                var valueCopy = value;
                var serialized = serializer.SerializeToArray(ref valueCopy);
                var deserializedValue = default(TField);
                serializer.Deserialize(serialized, ref deserializedValue);
                Assert.Equal(value, deserializedValue);
            }
        }

        [Fact]
        public void DirectAccessValueSerializerRoundTrip()
        {
            foreach (var value in TestValues)
            {
                var valueLocal = value;
                TestRoundTrippedValueViaValueSerializer(ref valueLocal);
            }
        }

        private void TestRoundTrippedValueViaValueSerializer(ref TField original)
        {
            var codecProvider = ServiceProvider.GetRequiredService<IValueSerializerProvider>();
            var serializer = codecProvider.GetValueSerializer<TField>();

            var pipe = new Pipe();
            using var writerSession = SessionPool.GetSession();
            var writer = Writer.Create(pipe.Writer, writerSession);
            var writerCodec = serializer;
            writerCodec.Serialize(ref writer, ref original);
            writer.WriteEndObject();
            writer.Commit();
            _ = pipe.Writer.FlushAsync().AsTask().GetAwaiter().GetResult();
            pipe.Writer.Complete();

            _ = pipe.Reader.TryRead(out var readResult);
            using var readerSession = SessionPool.GetSession();
            var reader = Reader.Create(readResult.Buffer, readerSession);
            var readerCodec = serializer;
            TField deserialized = default;
            readerCodec.Deserialize(ref reader, ref deserialized);
            pipe.Reader.AdvanceTo(readResult.Buffer.End);
            pipe.Reader.Complete();
            var isEqual = Equals(original, deserialized);
            Assert.True(
                isEqual,
                isEqual ? string.Empty : $"Deserialized value \"{deserialized}\" must equal original value \"{original}\"");
            Assert.Equal(writerSession.ReferencedObjects.CurrentReferenceId, readerSession.ReferencedObjects.CurrentReferenceId);
        }
    }
}