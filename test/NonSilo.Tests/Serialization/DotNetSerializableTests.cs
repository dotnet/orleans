using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using Orleans.Serialization;
using TestExtensions;
using Xunit;

namespace UnitTests.Serialization
{
    [TestCategory("BVT"), TestCategory("Serialization")]
    public class DotNetSerializableTests
    {
        private readonly SerializationTestEnvironment environment;

        public DotNetSerializableTests()
        {
            this.environment = SerializationTestEnvironment.Initialize();
        }

        /// <summary>
        /// Tests that <see cref="DotNetSerializableSerializer"/> can correctly serialize objects.
        /// </summary>
        /// <param name="serializerToUse"></param>
        [Fact]
        public void DotNetSerializableSerializerSerializesObjectWithCallbacks()
        {
            var input = new SimpleISerializableObject
            {
                Payload = "pyjamas"
            };

            // Verify that our behavior conforms to our expected behavior.
            var result = (SimpleISerializableObject) BuiltInSerializerTests.OrleansSerializationLoop(this.environment.SerializationManager, input);
            Assert.Equal(
                new[]
                {
                    "default_ctor",
                    "serializing",
                    "serialized"
                },
                input.History);
            Assert.Equal(3, input.Contexts.Count);
            Assert.All(input.Contexts, ctx => Assert.True(ctx.Context is ICopyContext || ctx.Context is ISerializationContext));

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
            Assert.All(result.Contexts, ctx => Assert.True(ctx.Context is IDeserializationContext));
        }

        /// <summary>
        /// Tests that <see cref="DotNetSerializableSerializer"/> can correctly serialize structs.
        /// </summary>
        /// <param name="serializerToUse"></param>
        [Fact]
        public void DotNetSerializableSerializerSerializesStructWithCallbacks()
        {
            var input = new SimpleISerializableStruct
            {
                Payload = "pyjamas"
            };

            // Verify that our behavior conforms to our expected behavior.
            var result = (SimpleISerializableStruct) BuiltInSerializerTests.OrleansSerializationLoop(this.environment.SerializationManager, input);
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
            Assert.All(result.Contexts, ctx => Assert.True(ctx.Context is IDeserializationContext));
        }

        [Serializable]
        public class SimpleISerializableObject : ISerializable, IDeserializationCallback
        {
            private List<string> history;
            private List<StreamingContext> contexts;

            public SimpleISerializableObject()
            {
                this.History.Add("default_ctor");
            }

            public SimpleISerializableObject(SerializationInfo info, StreamingContext context)
            {
                this.History.Add("serialization_ctor");
                this.Contexts.Add(context);
                this.Payload = info.GetString(nameof(this.Payload));
            }

            public List<string> History => this.history ?? (this.history = new List<string>());
            public List<StreamingContext> Contexts => this.contexts ?? (this.contexts = new List<StreamingContext>());

            public string Payload { get; set; }

            public void GetObjectData(SerializationInfo info, StreamingContext context)
            {
                this.Contexts.Add(context);
                info.AddValue(nameof(this.Payload), this.Payload);
            }

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
        public struct SimpleISerializableStruct : ISerializable, IDeserializationCallback
        {
            private List<string> history;
            private List<StreamingContext> contexts;

            public SimpleISerializableStruct(SerializationInfo info, StreamingContext context)
            {
                this.history = null;
                this.contexts = null;
                this.Payload = info.GetString(nameof(this.Payload));
                this.History.Add("serialization_ctor");
                this.Contexts.Add(context);
            }

            public List<string> History => this.history ?? (this.history = new List<string>());
            public List<StreamingContext> Contexts => this.contexts ?? (this.contexts = new List<StreamingContext>());

            public string Payload { get; set; }

            public void GetObjectData(SerializationInfo info, StreamingContext context)
            {
                this.Contexts.Add(context);
                info.AddValue(nameof(this.Payload), this.Payload);
            }

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
    }
}