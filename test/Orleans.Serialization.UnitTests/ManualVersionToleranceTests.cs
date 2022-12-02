using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Serialization.UnitTests
{
    public class ManualVersionToleranceTests
    {
        private const string TestString = "hello, Orleans.Serialization";
        private readonly ITestOutputHelper _log;
        private readonly IServiceProvider _serviceProvider;
        private readonly CodecProvider _codecProvider;
        private readonly IFieldCodec<SubType> _serializer;
        private readonly ServiceCollection _serviceCollection;

        public ManualVersionToleranceTests(ITestOutputHelper log)
        {
            _log = log;
            var serviceCollection = new ServiceCollection();
            _serviceCollection = serviceCollection;
            _ = _serviceCollection.AddSerializer(builder =>
              {
                  _ = builder.Configure(configuration =>
                    {
                        _ = configuration.Serializers.Add(typeof(SubTypeSerializer));
                        _ = configuration.Serializers.Add(typeof(BaseTypeSerializer));
                        _ = configuration.Serializers.Add(typeof(ObjectWithNewFieldTypeSerializer));
                        _ = configuration.Serializers.Add(typeof(ObjectWithoutNewFieldTypeSerializer));

                        // Intentionally remove the generated serializer for these type. It will be added back during tests.
                        configuration.Serializers.RemoveWhere(s => typeof(IFieldCodec<ObjectWithNewField>).IsAssignableFrom(s));
                        configuration.Serializers.RemoveWhere(s => typeof(IFieldCodec<ObjectWithoutNewField>).IsAssignableFrom(s));
                    });
              });

            _serviceProvider = _serviceCollection.BuildServiceProvider();

            _codecProvider = _serviceProvider.GetRequiredService<CodecProvider>();
            _serializer = _codecProvider.GetCodec<SubType>();
        }

        [Fact]
        public void VersionTolerance_RoundTrip_Tests()
        {
            RoundTripTest(
                new SubType
                {
                    BaseTypeString = "HOHOHO",
                    AddedLaterString = TestString,
                    String = null,
                    Int = 1,
                    Ref = TestString
                });

            RoundTripTest(
                new SubType
                {
                    BaseTypeString = "base",
                    String = "sub",
                    Int = 2,
                });

            RoundTripTest(
                new SubType
                {
                    BaseTypeString = "base",
                    String = "sub",
                    Int = int.MinValue,
                });

            RoundTripTest(
                new SubType
                {
                    BaseTypeString = TestString,
                    String = TestString,
                    Int = 10
                });

            RoundTripTest(
                new SubType
                {
                    BaseTypeString = TestString,
                    String = null,
                    Int = 1
                });

            RoundTripTest(
                new SubType
                {
                    BaseTypeString = TestString,
                    String = null,
                    Int = 1
                });

            TestSkip(
                new SubType
                {
                    BaseTypeString = TestString,
                    String = null,
                    Int = 1
                });

            var self = new SubType
            {
                BaseTypeString = "HOHOHO",
                AddedLaterString = TestString,
                String = null,
                Int = 1
            };
            self.Ref = self;
            RoundTripTest(self, assertRef: false);

            self.Ref = Guid.NewGuid();
            RoundTripTest(self, assertRef: false);
        }

        private SerializerSession GetSession() => _serviceProvider.GetRequiredService<SerializerSessionPool>().GetSession();

        private void RoundTripTest(SubType expected, bool assertRef = true)
        {
            using var writerSession = GetSession();
            var pipe = new Pipe();
            var writer = Writer.Create(pipe.Writer, writerSession);

            _serializer.WriteField(ref writer, 0, typeof(SubType), expected);
            writer.Commit();

            _log.WriteLine($"Size: {writer.Position} bytes.");
            _log.WriteLine($"Wrote References:\n{GetWriteReferenceTable(writerSession)}");

            _ = pipe.Writer.FlushAsync().AsTask().GetAwaiter().GetResult();
            pipe.Writer.Complete();
            _ = pipe.Reader.TryRead(out var readResult);
            using var readerSesssion = GetSession();
            var reader = Reader.Create(readResult.Buffer, readerSesssion);
            var initialHeader = reader.ReadFieldHeader();

            _log.WriteLine("Header:");
            _log.WriteLine(initialHeader.ToString());

            var actual = _serializer.ReadValue(ref reader, initialHeader);
            pipe.Reader.AdvanceTo(readResult.Buffer.End);
            pipe.Reader.Complete();

            _log.WriteLine($"Expect: {expected}\nActual: {actual}");

            Assert.Equal(expected.BaseTypeString, actual.BaseTypeString);
            Assert.Null(actual.AddedLaterString); // The deserializer isn't 'aware' of this field which was added later - version tolerance.
            Assert.Equal(expected.String, actual.String);
            Assert.Equal(expected.Int, actual.Int);
            if (assertRef)
            {
                Assert.Equal(expected.Ref, actual.Ref);
            }
            Assert.Equal(writer.Position, reader.Position);
            Assert.Equal(writer.Session.ReferencedObjects.CurrentReferenceId, reader.Session.ReferencedObjects.CurrentReferenceId);

            var references = GetReadReferenceTable(reader.Session);
            _log.WriteLine($"Read references:\n{references}");
        }

        private void TestSkip(SubType expected)
        {
            using var writerSession = GetSession();
            var pipe = new Pipe();
            var writer = Writer.Create(pipe.Writer, writerSession);

            _serializer.WriteField(ref writer, 0, typeof(SubType), expected);
            writer.Commit();

            _ = pipe.Writer.FlushAsync().AsTask().GetAwaiter().GetResult();
            var objectWithNewFieldSerializer = _codecProvider.GetCodec<ObjectWithNewField>();
            var objectWithoutNewFieldSerializer = _codecProvider.GetCodec<ObjectWithoutNewField>();
            pipe.Writer.Complete();
            _ = pipe.Reader.TryRead(out var readResult);
            using var readerSession = GetSession();
            var reader = Reader.Create(readResult.Buffer, readerSession);
            var initialHeader = reader.ReadFieldHeader();
            var skipCodec = new SkipFieldCodec();
            _ = skipCodec.ReadValue(ref reader, initialHeader);
            pipe.Reader.AdvanceTo(readResult.Buffer.End);
            pipe.Reader.Complete();
            Assert.Equal(writer.Session.ReferencedObjects.CurrentReferenceId, reader.Session.ReferencedObjects.CurrentReferenceId);
            _log.WriteLine($"Skipped {reader.Position} bytes.");
        }

        private static StringBuilder GetReadReferenceTable(SerializerSession session)
        {
            var table = session.ReferencedObjects.CopyReferenceTable();
            var references = new StringBuilder();
            foreach (var entry in table)
            {
                _ = references.AppendLine($"\t[{entry.Key}] {entry.Value}");
            }
            return references;
        }

        private static StringBuilder GetWriteReferenceTable(SerializerSession session)
        {
            var table = session.ReferencedObjects.CopyIdTable();
            var references = new StringBuilder();
            foreach (var entry in table)
            {
                _ = references.AppendLine($"\t[{entry.Value}] {entry.Key}");
            }
            return references;
        }

        [Fact]
        public void ObjectWithNewFieldTest()
        {
            var expected = new ObjectWithNewField("blah", newField: "this field will not be manually serialized -- the binary will not have it!");

            using var writerSession = GetSession();
            var pipe = new Pipe();
            var writer = Writer.Create(pipe.Writer, writerSession);

            // Using manual serializer that ignores ObjectWithNewField.NewField
            // not serializing NewField to simulate a binary that's created from a previous version of the object
            var objectWithNewFieldSerializer = _codecProvider.GetCodec<ObjectWithNewField>();
            var objectWithoutNewFieldSerializer = _codecProvider.GetCodec<ObjectWithoutNewField>();
            _ = Assert.IsType<ConcreteTypeSerializer<ObjectWithNewField, ObjectWithNewFieldTypeSerializer>>(objectWithNewFieldSerializer);
            objectWithNewFieldSerializer.WriteField(ref writer, 0, typeof(ObjectWithNewField), expected);
            writer.Commit();

            _log.WriteLine($"Size: {writer.Position} bytes.");
            _log.WriteLine($"Wrote References:\n{GetWriteReferenceTable(writerSession)}");

            _ = pipe.Writer.FlushAsync().AsTask().GetAwaiter().GetResult();
            pipe.Writer.Complete();

            _ = pipe.Reader.TryRead(out var readResult);
            using var readerSession = GetSession();
            var reader = Reader.Create(readResult.Buffer, readerSession);
            var initialHeader = reader.ReadFieldHeader();

            _log.WriteLine("Header:");
            _log.WriteLine(initialHeader.ToString());

            GetGeneratedSerializer(out objectWithNewFieldSerializer);
            Assert.IsNotType<ConcreteTypeSerializer<ObjectWithNewField, ObjectWithNewFieldTypeSerializer>>(objectWithNewFieldSerializer);

            // using Generated Deserializer, which is capable of deserializing NewField 
            var actual = objectWithNewFieldSerializer.ReadValue(ref reader, initialHeader);
            pipe.Reader.AdvanceTo(readResult.Buffer.End);
            pipe.Reader.Complete();

            _log.WriteLine($"Expect: {expected}\nActual: {actual}");

            Assert.Equal(expected.Blah, actual.Blah);
            objectWithNewFieldSerializer = _codecProvider.GetCodec<ObjectWithNewField>();
            objectWithoutNewFieldSerializer = _codecProvider.GetCodec<ObjectWithoutNewField>();
            Assert.Null(actual.NewField); // Null, since it should not be in the binary
            Assert.Equal(expected.Version, actual.Version);
            Assert.Equal(writer.Position, reader.Position);
            Assert.Equal(writer.Session.ReferencedObjects.CurrentReferenceId, reader.Session.ReferencedObjects.CurrentReferenceId);

            var references = GetReadReferenceTable(reader.Session);
            _log.WriteLine($"Read references:\n{references}");
        }

        [Fact]
        public void ObjectWithoutNewFieldTest()
        {
            var expected = new ObjectWithoutNewField("blah");

            using var writerSession = GetSession();
            var pipe = new Pipe();
            var writer = Writer.Create(pipe.Writer, writerSession);

            var objectWithNewFieldSerializer = _codecProvider.GetCodec<ObjectWithNewField>();
            var objectWithoutNewFieldSerializer = _codecProvider.GetCodec<ObjectWithoutNewField>();
            // Using a manual serializer that writes a new field
            // serializing a new field to simulate a binary that created from a newer version of the object
            _ = Assert.IsType<ConcreteTypeSerializer<ObjectWithoutNewField, ObjectWithoutNewFieldTypeSerializer>>(objectWithoutNewFieldSerializer);
            objectWithoutNewFieldSerializer.WriteField(ref writer, 0, typeof(ObjectWithoutNewField), expected);
            writer.Commit();

            _log.WriteLine($"Size: {writer.Position} bytes.");
            _log.WriteLine($"Wrote References:\n{GetWriteReferenceTable(writerSession)}");

            _ = pipe.Writer.FlushAsync().AsTask().GetAwaiter().GetResult();
            pipe.Writer.Complete();

            _ = pipe.Reader.TryRead(out var readResult);
            using var readerSession = GetSession();
            var reader = Reader.Create(readResult.Buffer, readerSession);
            var initialHeader = reader.ReadFieldHeader();

            _log.WriteLine("Header:");
            _log.WriteLine(initialHeader.ToString());

            GetGeneratedSerializer(out objectWithoutNewFieldSerializer);
            Assert.IsNotType<ConcreteTypeSerializer<ObjectWithoutNewField, ObjectWithoutNewFieldTypeSerializer>>(objectWithoutNewFieldSerializer);

            // using Generated Deserializer, which is not able to deserialize the new field that was serialized
            var actual = objectWithoutNewFieldSerializer.ReadValue(ref reader, initialHeader);
            pipe.Reader.AdvanceTo(readResult.Buffer.End);
            pipe.Reader.Complete();

            _log.WriteLine($"Expect: {expected}\nActual: {actual}");

            Assert.Equal(expected.Blah, actual.Blah);
            Assert.Equal(expected.Version, actual.Version);
            Assert.Equal(writer.Position, reader.Position);
            Assert.Equal(writer.Session.ReferencedObjects.CurrentReferenceId, reader.Session.ReferencedObjects.CurrentReferenceId);

            var references = GetReadReferenceTable(reader.Session);
            _log.WriteLine($"Read references:\n{references}");
        }

        private void GetGeneratedSerializer<T>(out IFieldCodec<T> serializer)
        {
            var services = new ServiceCollection().AddSerializer();
            var serviceProvider = services.BuildServiceProvider();
            var codecProvider = serviceProvider.GetRequiredService<CodecProvider>();
            serializer = codecProvider.GetCodec<T>();
        }

        [GenerateSerializer]
        public class ObjectWithNewField
        {
            [Id(0)]
            public string Blah { get; set; }
            [Id(1)]
            public object NewField { get; set; }
            [Id(2)]
            public int Version { get; set; }

            public ObjectWithNewField(string blah, object newField)
            {
                Blah = blah;
                NewField = newField;
                Version = 2;
            }

            public override string ToString() => $"{nameof(Blah)}: {Blah}; {nameof(NewField)}: {NewField}; {nameof(Version)}: {Version}";
        }

        public class ObjectWithNewFieldTypeSerializer : IBaseCodec<ObjectWithNewField>
        {
            public void Serialize<TBufferWriter>(ref Writer<TBufferWriter> writer, ObjectWithNewField obj) where TBufferWriter : IBufferWriter<byte>
            {
                // not serializing newField to simulate a binary that's created from a previous version of the object
                StringCodec.WriteField(ref writer, 0, obj.Blah);
                Int32Codec.WriteField(ref writer, 2, obj.Version);
            }

            // using a generated deserializer for deserialization
            public void Deserialize<TInput>(ref Reader<TInput> reader, ObjectWithNewField obj)
            {
            }
        }


        [GenerateSerializer]
        public class ObjectWithoutNewField
        {
            [Id(0)]
            public string Blah { get; set; }
            [Id(1)]
            public int Version { get; set; }

            public ObjectWithoutNewField(string blah)
            {
                Blah = blah;
                Version = 1;
            }

            public override string ToString() => $"{nameof(Blah)}: {Blah}; {nameof(Version)}: {Version}";
        }

        public class ObjectWithoutNewFieldTypeSerializer : IBaseCodec<ObjectWithoutNewField>
        {
            public void Serialize<TBufferWriter>(ref Writer<TBufferWriter> writer, ObjectWithoutNewField obj) where TBufferWriter : IBufferWriter<byte>
            {
                StringCodec.WriteField(ref writer, 0, obj.Blah);
                Int32Codec.WriteField(ref writer, 1, obj.Version);
                // serializing a new field to simulate a binary that's created from a newer version of the object
                ObjectCodec.WriteField(ref writer, 6, "I will be stuck in binary limbo! (I shouldn't be part of the deserialized object)");
            }

            // using a generated deserializer for deserialization
            public void Deserialize<TInput>(ref Reader<TInput> reader, ObjectWithoutNewField obj)
            {
            }
        }

        /// <summary>
        /// NOTE: The serializer for this type is HAND-ROLLED. See <see cref="BaseTypeSerializer" />
        /// </summary>
        public class BaseType : IEquatable<BaseType>
        {
            public string BaseTypeString { get; set; }
            public string AddedLaterString { get; set; }

            public bool Equals(BaseType other) => other is not null
                    && string.Equals(BaseTypeString, other.BaseTypeString, StringComparison.Ordinal)
                    && string.Equals(AddedLaterString, other.AddedLaterString, StringComparison.Ordinal);

            public override bool Equals(object obj) => obj is BaseType baseType && Equals(baseType);

            public override int GetHashCode() => HashCode.Combine(BaseTypeString, AddedLaterString);

            public override string ToString() => $"{nameof(BaseTypeString)}: {BaseTypeString}";
        }

        /// <summary>
        /// NOTE: The serializer for this type is HAND-ROLLED. See <see cref="SubTypeSerializer" />
        /// </summary>
        public class SubType : BaseType, IEquatable<SubType>
        {
            // 0
            public string String { get; set; }

            // 1
            public int Int { get; set; }

            // 3
            public object Ref { get; set; }

            public bool Equals(SubType other)
            {
                if (other is null)
                {
                    return false;
                }

                return
                    base.Equals(other)
                    && string.Equals(String, other.String, StringComparison.Ordinal)
                    && Int == other.Int
                    && (ReferenceEquals(Ref, other.Ref) || Ref.Equals(other.Ref));
            }

            public override string ToString()
            {
                string refString = Ref == this ? "[this]" : $"[{Ref?.ToString() ?? "null"}]";
                return $"{base.ToString()}, {nameof(String)}: {String}, {nameof(Int)}: {Int}, Ref: {refString}";
            }

            public override bool Equals(object obj) => obj is SubType subType && Equals(subType);

            public override int GetHashCode()
            {
                // Avoid stack overflows with this one weird trick.
                if (ReferenceEquals(Ref, this))
                {
                    return HashCode.Combine(base.GetHashCode(), String, Int);
                }

                return HashCode.Combine(base.GetHashCode(), String, Int, Ref);
            }
        }

        public class SubTypeSerializer : IBaseCodec<SubType>
        {
            private readonly IBaseCodec<BaseType> _baseTypeSerializer;
            private readonly IFieldCodec<string> _stringCodec;
            private readonly IFieldCodec<int> _intCodec;
            private readonly IFieldCodec<object> _objectCodec;

            public SubTypeSerializer(IBaseCodec<BaseType> baseTypeSerializer, IFieldCodec<string> stringCodec, IFieldCodec<int> intCodec, IFieldCodec<object> objectCodec)
            {
                _baseTypeSerializer = OrleansGeneratedCodeHelper.UnwrapService(this, baseTypeSerializer);
                _stringCodec = OrleansGeneratedCodeHelper.UnwrapService(this, stringCodec);
                _intCodec = OrleansGeneratedCodeHelper.UnwrapService(this, intCodec);
                _objectCodec = OrleansGeneratedCodeHelper.UnwrapService(this, objectCodec);
            }

            public void Serialize<TBufferWriter>(ref Writer<TBufferWriter> writer, SubType obj) where TBufferWriter : IBufferWriter<byte>
            {
                _baseTypeSerializer.Serialize(ref writer, obj);
                writer.WriteEndBase(); // the base object is complete.

                _stringCodec.WriteField(ref writer, 0, typeof(string), obj.String);
                _intCodec.WriteField(ref writer, 1, typeof(int), obj.Int);
                _objectCodec.WriteField(ref writer, 1, typeof(object), obj.Ref);
                _intCodec.WriteField(ref writer, 1, typeof(int), obj.Int);
                _intCodec.WriteField(ref writer, 409, typeof(int), obj.Int);
                /*writer.WriteFieldHeader(session, 1025, typeof(Guid), Guid.Empty.GetType(), WireType.Fixed128);
                writer.WriteFieldHeader(session, 1020, typeof(object), typeof(Program), WireType.Reference);*/
            }

            public void Deserialize<TInput>(ref Reader<TInput> reader, SubType obj)
            {
                uint fieldId = 0;
                _baseTypeSerializer.Deserialize(ref reader, obj);
                while (true)
                {
                    var header = reader.ReadFieldHeader();
                    if (header.IsEndBaseOrEndObject)
                    {
                        break;
                    }

                    fieldId += header.FieldIdDelta;
                    switch (fieldId)
                    {
                        case 0:
                            obj.String = _stringCodec.ReadValue(ref reader, header);
                            break;
                        case 1:
                            obj.Int = _intCodec.ReadValue(ref reader, header);
                            break;
                        case 2:
                            obj.Ref = _objectCodec.ReadValue(ref reader, header);
                            break;
                        default:
                            reader.ConsumeUnknownField(header);
                            break;
                    }
                }
            }
        }

        public class BaseTypeSerializer : IBaseCodec<BaseType>
        {
            public void Serialize<TBufferWriter>(ref Writer<TBufferWriter> writer, BaseType obj) where TBufferWriter : IBufferWriter<byte>
            {
                StringCodec.WriteField(ref writer, 0, obj.BaseTypeString);
                StringCodec.WriteField(ref writer, 234, obj.AddedLaterString);
            }

            public void Deserialize<TInput>(ref Reader<TInput> reader, BaseType obj)
            {
                uint fieldId = 0;
                while (true)
                {
                    var header = reader.ReadFieldHeader();
                    if (header.IsEndBaseOrEndObject)
                    {
                        break;
                    }

                    fieldId += header.FieldIdDelta;
                    switch (fieldId)
                    {
                        case 0:
                            obj.BaseTypeString = StringCodec.ReadValue(ref reader, header);
                            break;
                        default:
                            reader.ConsumeUnknownField(header);
                            break;
                    }
                }
            }
        }
    }
}