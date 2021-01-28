using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.Serialization;
using Orleans.Runtime;
using Orleans.Serialization;
using TestExtensions;
using Xunit;

namespace UnitTests.Serialization
{
    [TestCategory("BVT"), TestCategory("Serialization")]
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    public class ILSerializerTests
    {
        private readonly TestEnvironmentFixture fixture;

        public ILSerializerTests(TestEnvironmentFixture fixture)
        {
            this.fixture = fixture;
        }

        /// <summary>
        /// Tests that <see cref="ILSerializerGenerator"/> supports distinct field selection for serialization
        /// versus copy operations.
        /// </summary>
        [Fact]
        public void ILSerializer_AllowCopiedFieldsToDifferFromSerializedFields()
        {
            var input = new FieldTest
            {
                One = 1,
                Two = 2,
                Three = 3
            };

            var generator = new ILSerializerGenerator();
            var serializers = generator.GenerateSerializer(input.GetType(), f => f.Name != "One", f => f.Name != "Three");
            var writer = new BinaryTokenStreamWriter();
            var context = new SerializationContext(this.fixture.SerializationManager)
            {
                StreamWriter = writer
            };
            var copy = (FieldTest)serializers.DeepCopy(input, context);
            Assert.Equal(1, copy.One);
            Assert.Equal(2, copy.Two);
            Assert.Equal(0, copy.Three);
            
            serializers.Serialize(input, context, input.GetType());
            var reader = new DeserializationContext(this.fixture.SerializationManager)
            {
                StreamReader = new BinaryTokenStreamReader(writer.ToByteArray())
            };
            var deserialized = (FieldTest)serializers.Deserialize(input.GetType(), reader);

            Assert.Equal(0, deserialized.One);
            Assert.Equal(2, deserialized.Two);
            Assert.Equal(3, deserialized.Three);
        }

        /// <summary>
        /// Tests that <see cref="ILSerializerGenerator"/> supports the <see cref="IOnDeserialized"/> lifecycle hook.
        /// </summary>
        [Fact]
        public void ILSerializer_LifecycleHooksAreCalled()
        {
            var input = new FieldTest();
            var generator = new ILSerializerGenerator();
            var serializers = generator.GenerateSerializer(input.GetType());
            var writer = new BinaryTokenStreamWriter();
            var context = new SerializationContext(this.fixture.SerializationManager)
            {
                StreamWriter = writer
            };
            serializers.Serialize(input, context, input.GetType());
            var reader = new DeserializationContext(this.fixture.SerializationManager)
            {
                StreamReader = new BinaryTokenStreamReader(writer.ToByteArray())
            };
            var deserialized = (FieldTest)serializers.Deserialize(input.GetType(), reader);

            Assert.Null(input.Context);
            Assert.NotNull(deserialized.Context);
            Assert.Equal(this.fixture.SerializationManager, deserialized.Context.GetSerializationManager());
        }

        /// <summary>
        /// Tests that <see cref="ILSerializerGenerator"/> does not serialize fields marked as [NonSerialized].
        /// </summary>
        [Fact]
        public void ILSerializer_NonSerializedFields()
        {
            var input = new FieldTest
            {
                One = 1,
                Two = 2,
                NonSerializedInt = 1098
            };
            var generator = new ILSerializerGenerator();
            var serializers = generator.GenerateSerializer(input.GetType());
            var writer = new BinaryTokenStreamWriter();
            var context = new SerializationContext(this.fixture.SerializationManager)
            {
                StreamWriter = writer
            };
            serializers.Serialize(input, context, input.GetType());
            var reader = new DeserializationContext(this.fixture.SerializationManager)
            {
                StreamReader = new BinaryTokenStreamReader(writer.ToByteArray())
            };
            var deserialized = (FieldTest) serializers.Deserialize(input.GetType(), reader);

            Assert.Equal(input.One, deserialized.One);
            Assert.Equal(input.Two, deserialized.Two);
            Assert.NotEqual(input.NonSerializedInt, deserialized.NonSerializedInt);
            Assert.Equal(default(int), deserialized.NonSerializedInt);
        }

        /// <summary>
        /// Tests that <see cref="ILBasedSerializer"/> can correctly serialize objects which have serialization lifecycle hooks.
        /// </summary>
        [Fact]
        public void ILSerializer_SerializesObjectWithHooks()
        {
            var input = new SimpleISerializableObject
            {
                Payload = "pyjamas"
            };

            // Verify that our behavior conforms to our expected behavior.
            var result = SerializerLoop(input);
            Assert.Equal(
                new[]
                {
                    "default_ctor",
                    "serializing",
                    "serialized"
                },
                input.History);
            Assert.Equal(2, input.Contexts.Count);
            Assert.All(input.Contexts,
                ctx => Assert.True(ctx.Context is ICopyContext || ctx.Context is ISerializationContext));

            Assert.Equal(
                new[]
                {
                    "default_ctor",
                    "deserializing",
                    "deserialized",
                    "deserialization"
                },
                result.History);
            Assert.Equal(input.Payload, result.Payload, StringComparer.Ordinal);
            Assert.Equal(2, result.Contexts.Count);
            Assert.All(result.Contexts, ctx => Assert.True(ctx.Context is IDeserializationContext));
        }

        /// <summary>
        /// Tests that <see cref="ILBasedSerializer"/> can correctly serialize structs which have serialization lifecycle hooks.
        /// </summary>
        [Fact]
        public void ILSerializer_SerializesStructWithHooks()
        {
            var input = new SimpleISerializableStruct
            {
                Payload = "pyjamas"
            };

            // Verify that our behavior conforms to our expected behavior.
            var result = SerializerLoop(input);
            Assert.Equal(
                new[]
                {
                    "deserializing",
                    "deserialized",
                    "deserialization"
                },
                result.History);
            Assert.Equal(input.Payload, result.Payload, StringComparer.Ordinal);
            Assert.Equal(2, result.Contexts.Count);
            Assert.All(result.Contexts, ctx => Assert.True(ctx.Context is IDeserializationContext));
        }

        private T SerializerLoop<T>(T input)
        {
#pragma warning disable CS0618 // Type or member is obsolete
            var serializer = new ILBasedSerializer(new CachedTypeResolver());
#pragma warning restore CS0618 // Type or member is obsolete
            Assert.True(serializer.IsSupportedType(input.GetType()));

            var writer = new BinaryTokenStreamWriter();
            var serializationContext =
                new SerializationContext(this.fixture.SerializationManager)
                {
                    StreamWriter = writer
                };
            serializer.Serialize(input, serializationContext, typeof(T));
            var deserializationContext = new DeserializationContext(this.fixture.SerializationManager)
            {
                StreamReader = new BinaryTokenStreamReader(writer.ToBytes())
            };

            return (T) serializer.Deserialize(typeof(T), deserializationContext);
        }

        [Serializable]
        public class SimpleISerializableObject : IDeserializationCallback
        {
            [NonSerialized]
            private List<string> history;

            [NonSerialized]
            private List<StreamingContext> contexts;

            public SimpleISerializableObject()
            {
                this.History.Add("default_ctor");
            }
            
            public List<string> History => this.history ?? (this.history = new List<string>());
            public List<StreamingContext> Contexts => this.contexts ?? (this.contexts = new List<StreamingContext>());

            public string Payload { get; set; }
            
            [OnSerializing]
            internal void OnSerializingMethod(StreamingContext context)
            {
                this.History.Add("serializing");
                this.Contexts.Add(context);
            }

            [OnSerialized]
            internal void OnSerializedMethod(StreamingContext context)
            {
                this.History.Add("serialized");
                this.Contexts.Add(context);
            }

            [OnDeserializing]
            internal void OnDeserializingMethod(StreamingContext context)
            {
                this.History.Add("deserializing");
                this.Contexts.Add(context);
            }

            [OnDeserialized]
            internal void OnDeserializedMethod(StreamingContext context)
            {
                this.History.Add("deserialized");
                this.Contexts.Add(context);
            }

            void IDeserializationCallback.OnDeserialization(object sender)
            {
                this.History.Add("deserialization");
            }
        }

        [Serializable]
        public struct SimpleISerializableStruct : IDeserializationCallback
        {
            [NonSerialized]
            private List<string> history;

            [NonSerialized]
            private List<StreamingContext> contexts;
            
            public List<string> History => this.history ?? (this.history = new List<string>());
            public List<StreamingContext> Contexts => this.contexts ?? (this.contexts = new List<StreamingContext>());

            public string Payload { get; set; }

            [OnSerializing]
            internal void OnSerializingMethod(StreamingContext context)
            {
                this.History.Add("serializing");
                this.Contexts.Add(context);
            }

            [OnSerialized]
            internal void OnSerializedMethod(StreamingContext context)
            {
                this.History.Add("serialized");
                this.Contexts.Add(context);
            }

            [OnDeserializing]
            internal void OnDeserializingMethod(StreamingContext context)
            {
                this.History.Add("deserializing");
                this.Contexts.Add(context);
            }

            [OnDeserialized]
            internal void OnDeserializedMethod(StreamingContext context)
            {
                this.History.Add("deserialized");
                this.Contexts.Add(context);
            }

            void IDeserializationCallback.OnDeserialization(object sender)
            {
                this.History.Add("deserialization");
            }
        }

        [SuppressMessage("ReSharper", "StyleCop.SA1401", Justification = "This is for testing purposes.")]
        private class FieldTest : IOnDeserialized
        {
#pragma warning disable 169
            public int One;
            public int Two;
            public int Three;

            [NonSerialized]
            public int NonSerializedInt;

            [NonSerialized]
            public ISerializerContext Context;

            public void OnDeserialized(ISerializerContext context)
            {
                this.Context = context;
            }
#pragma warning restore 169
        }
    }
}