using Orleans.Serialization.Buffers;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
using Orleans.Serialization.TypeSystem;
using Orleans.Serialization.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Serialization.UnitTests
{
    [Trait("Category", "BVT"), Trait("Category", "ISerializable")]
    public class ISerializableTests
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly SerializerSessionPool _sessionPool;
        private readonly Serializer<object> _serializer;
        private readonly ITestOutputHelper _log;

        public ISerializableTests(ITestOutputHelper log)
        {
            var services = new ServiceCollection();
            _ = services.AddSerializer();
            services.RemoveAll(typeof(TypeResolver));
            services.AddSingleton<TypeResolver>(sp => new BanningTypeResolver(typeof(UnserializableConformingException), typeof(UnserializableNonConformingException)));
            services.AddSingleton<IGeneralizedCodec, DotNetSerializableCodec>();

            _serviceProvider = services.BuildServiceProvider();
            _sessionPool = _serviceProvider.GetService<SerializerSessionPool>();
            _serializer = _serviceProvider.GetRequiredService<Serializer<object>>();
            _log = log;
        }

        private object SerializationLoop(object original)
        {
            var pipe = new Pipe();

            using var writerSession = _sessionPool.GetSession();
            var writer = Writer.Create(pipe.Writer, writerSession);
            _serializer.Serialize(original, ref writer);
            _ = pipe.Writer.FlushAsync().AsTask().GetAwaiter().GetResult();
            pipe.Writer.Complete();

            _ = pipe.Reader.TryRead(out var readResult);
            {
                using var readerSession = _sessionPool.GetSession();
                var reader = Reader.Create(readResult.Buffer, readerSession);
                var output = BitStreamFormatter.Format(ref reader);
                _log.WriteLine(output);
            }

            {
                using var readerSession = _sessionPool.GetSession();
                var reader = Reader.Create(readResult.Buffer, readerSession);
                var deserialized = _serializer.Deserialize(ref reader);
                pipe.Reader.AdvanceTo(readResult.Buffer.End);
                pipe.Reader.Complete();

                //Assert.True(Equals(original, deserialized), $"Deserialized value \"{deserialized}\" must equal original value \"{original}\"");
                Assert.Equal(writer.Position, reader.Position);
                Assert.Equal(writerSession.ReferencedObjects.CurrentReferenceId, readerSession.ReferencedObjects.CurrentReferenceId);
                return deserialized;
            }
        }

        /// <summary>
        /// Tests that <see cref="DotNetSerializableCodec"/> can correctly serialize objects.
        /// </summary>
        [Fact]
        public void ISerializableObjectWithCallbacks()
        {
            var input = new SimpleISerializableObject
            {
                Payload = "pyjamas"
            };

            // Verify that our behavior conforms to our expected behavior.
            var result = (SimpleISerializableObject)SerializationLoop(input);
            Assert.Equal(
                new[]
                {
                    "default_ctor",
                    "serializing",
                    "serialized"
                },
                input.History);
            Assert.Equal(3, input.Contexts.Count);

            Assert.Equal(
                new[]
                {
                    "deserializing",
                    "serialization_ctor",
                    "deserialized",
                    "deserialization"
                },
                result.History);
            Assert.Equal(input.Payload, result.Payload, StringComparer.Ordinal);
            Assert.Equal(3, result.Contexts.Count);
        }

        /// <summary>
        /// Tests that <see cref="DotNetSerializableCodec"/> can correctly serialize structs.
        /// </summary>
        [Fact]
        public void ISerializableStructWithCallbacks()
        {
            var input = new SimpleISerializableStruct
            {
                Payload = "pyjamas"
            };

            // Verify that our behavior conforms to our expected behavior.
            var result = (SimpleISerializableStruct)SerializationLoop(input);
            Assert.Equal(
                new[]
                {
                    "serialization_ctor",
                    "deserialized",
                    "deserialization"
                },
                result.History);
            Assert.Equal(input.Payload, result.Payload, StringComparer.Ordinal);
            Assert.Equal(2, result.Contexts.Count);
        }

        private class BaseException : Exception
        {
            public BaseException() { }

            public BaseException(string message, Exception innerException) : base(message, innerException) { }
#if NET8_0_OR_GREATER
            [Obsolete]
#endif
            protected BaseException(SerializationInfo info, StreamingContext context) : base(info, context)
            {
                BaseField = (SimpleISerializableObject)info.GetValue("BaseField", typeof(SimpleISerializableObject));
            }

            public SimpleISerializableObject BaseField { get; set; }

#if NET8_0_OR_GREATER
            [Obsolete]
#endif
            public override void GetObjectData(SerializationInfo info, StreamingContext context)
            {
                base.GetObjectData(info, context);
                info.AddValue("BaseField", BaseField, typeof(SimpleISerializableObject));
            }
        }

        [Serializable]
        private class UnserializableConformingException : BaseException
        {
            public string SubClassField { get; set; }
            public object SomeObject { get; set; }

            public UnserializableConformingException(string message, Exception innerException) : base(message, innerException) { }

#if NET8_0_OR_GREATER
            [Obsolete]
#endif
            protected UnserializableConformingException(SerializationInfo info, StreamingContext context) : base(info, context)
            {
                SubClassField = info.GetString("SubClassField");
                SomeObject = info.GetValue("SomeObject", typeof(object));
            }

#if NET8_0_OR_GREATER
            [Obsolete]
#endif
            public override void GetObjectData(SerializationInfo info, StreamingContext context)
            {
                base.GetObjectData(info, context);
                info.AddValue("SubClassField", SubClassField);
                info.AddValue("SomeObject", SomeObject, typeof(object));
            }
        }

        public class UnserializableNonConformingException : Exception
        {
            public UnserializableNonConformingException(string message) : base(message)
            { }
        }

        private class BanningTypeResolver : TypeResolver
        {
            private readonly TypeResolver _resolver = new CachedTypeResolver();
            private readonly HashSet<Type> _blockedTypes;

            public BanningTypeResolver(params Type[] blockedTypes)
            {
                _blockedTypes = new HashSet<Type>();
                foreach (var type in blockedTypes ?? Array.Empty<Type>())
                {
                    _blockedTypes.Add(type);
                }
            }

            public override Type ResolveType(string name)
            {
                var result = _resolver.ResolveType(name);
                if (_blockedTypes.Contains(result))
                {
                    result = null;
                }

                return result;
            }

            public override bool TryResolveType(string name, out Type type)
            {
                if (_resolver.TryResolveType(name, out type))
                {
                    if (_blockedTypes.Contains(type))
                    {
                        type = null;
                        return false;
                    }

                    return true;
                }

                return false;
            }
        }

        [Fact]
        public void Serialize_UnserializableException()
        {
            const string message = "This is a test message";

            var serializer = _serviceProvider.GetRequiredService<Serializer>();

            // Throw the exception so that stack trace is populated
            Exception source = Assert.Throws<UnserializableNonConformingException>((Action)(() =>
            {
                throw new UnserializableNonConformingException(message);
            }));

            var serialized = serializer.SerializeToArray(source);
            using var formatterSession = _sessionPool.GetSession();
            var formatted = BitStreamFormatter.Format(serialized, formatterSession);

            object deserialized = serializer.Deserialize<Exception>(serialized);

            // Type is wrong after round trip of unserializable exception
            var result = Assert.IsAssignableFrom<UnavailableExceptionFallbackException>(deserialized);

            // Exception message is correct after round trip of unserializable exception
            Assert.Contains(message, result.Message);
            Assert.Equal(RuntimeTypeNameFormatter.Format(source.GetType()), result.ExceptionType);

            // Throw the exception so that stack trace is populated
            source = Assert.Throws<UnserializableConformingException>((Action)(() =>
            {
                Exception inner;
                try
                {
                    throw new InvalidOperationException("invalid");
                }
                catch (Exception exception)
                {
                    inner = exception;
                }

                throw new UnserializableConformingException(message, inner)
                {
                    SomeObject = new object(),
                    SubClassField = "hoppo",
                    BaseField = new SimpleISerializableObject() { Payload = "payload" }
                };
            }));
            deserialized = serializer.Deserialize<Exception>(serializer.SerializeToArray(source));

            // Type is wrong after round trip of unserializable exception
            result = Assert.IsAssignableFrom<UnavailableExceptionFallbackException>(deserialized);

            // Exception message is correct after round trip of unserializable exception
            Assert.Contains(message, result.Message);
            Assert.Equal(RuntimeTypeNameFormatter.Format(source.GetType()), result.ExceptionType);

            var inner = Assert.IsType<InvalidOperationException>(result.InnerException);
            Assert.Equal("invalid", inner.Message);

            Assert.True(result.Properties.ContainsKey("SomeObject"));
            var baseField = Assert.IsType<SimpleISerializableObject>(result.Properties["BaseField"]);
            Assert.Equal("payload", baseField.Payload);
        }

        [Serializable]
        public class SimpleISerializableObject : ISerializable, IDeserializationCallback
        {
            private List<string> _history;
            private List<StreamingContext> _contexts;

            public SimpleISerializableObject()
            {
                History.Add("default_ctor");
            }

            public SimpleISerializableObject(SerializationInfo info, StreamingContext context)
            {
                History.Add("serialization_ctor");
                Contexts.Add(context);
                Payload = info.GetString(nameof(Payload));
            }

            public List<string> History => _history ??= new List<string>();
            public List<StreamingContext> Contexts => _contexts ??= new List<StreamingContext>();

            public string Payload { get; set; }

            public void GetObjectData(SerializationInfo info, StreamingContext context)
            {
                Contexts.Add(context);
                info.AddValue(nameof(Payload), Payload);
            }

            [OnSerializing]
            internal void OnSerializingMethod(StreamingContext context)
            {
                History.Add("serializing");
                Contexts.Add(context);
            }

            [OnSerialized]
            internal void OnSerializedMethod(StreamingContext context)
            {
                History.Add("serialized");
                Contexts.Add(context);
            }

            [OnDeserializing]
            internal void OnDeserializingMethod(StreamingContext context)
            {
                History.Add("deserializing");
                Contexts.Add(context);
            }

            [OnDeserialized]
            internal void OnDeserializedMethod(StreamingContext context)
            {
                History.Add("deserialized");
                Contexts.Add(context);
            }

            void IDeserializationCallback.OnDeserialization(object sender) => History.Add("deserialization");
        }

        [Serializable]
        public struct SimpleISerializableStruct : ISerializable, IDeserializationCallback
        {
            private List<string> _history;
            private List<StreamingContext> _contexts;

            public SimpleISerializableStruct(SerializationInfo info, StreamingContext context)
            {
                _history = null;
                _contexts = null;
                Payload = info.GetString(nameof(Payload));
                History.Add("serialization_ctor");
                Contexts.Add(context);
            }

            public List<string> History => _history ??= new List<string>();
            public List<StreamingContext> Contexts => _contexts ??= new List<StreamingContext>();

            public string Payload { get; set; }

            public void GetObjectData(SerializationInfo info, StreamingContext context)
            {
                Contexts.Add(context);
                info.AddValue(nameof(Payload), Payload);
            }

            [OnSerializing]
            internal void OnSerializingMethod(StreamingContext context)
            {
                History.Add("serializing");
                Contexts.Add(context);
            }

            [OnSerialized]
            internal void OnSerializedMethod(StreamingContext context)
            {
                History.Add("serialized");
                Contexts.Add(context);
            }

            [OnDeserializing]
            internal void OnDeserializingMethod(StreamingContext context)
            {
                History.Add("deserializing");
                Contexts.Add(context);
            }

            [OnDeserialized]
            internal void OnDeserializedMethod(StreamingContext context)
            {
                History.Add("deserialized");
                Contexts.Add(context);
            }

            void IDeserializationCallback.OnDeserialization(object sender) => History.Add("deserialization");
        }
    }
}