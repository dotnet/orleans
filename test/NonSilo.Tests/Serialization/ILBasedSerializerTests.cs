using System;
using TestExtensions;

namespace UnitTests.Serialization
{
    using System.Diagnostics.CodeAnalysis;

    using Orleans.Serialization;

    using Xunit;

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
            var writer = new SerializationContext(this.fixture.SerializationManager)
            {
                StreamWriter = new BinaryTokenStreamWriter()
            };
            var copy = (FieldTest)serializers.DeepCopy(input, writer);
            Assert.Equal(1, copy.One);
            Assert.Equal(2, copy.Two);
            Assert.Equal(0, copy.Three);
            
            serializers.Serialize(input, writer, input.GetType());
            var reader = new DeserializationContext(this.fixture.SerializationManager)
            {
                StreamReader = new BinaryTokenStreamReader(writer.StreamWriter.ToByteArray())
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
            var writer = new SerializationContext(this.fixture.SerializationManager)
            {
                StreamWriter = new BinaryTokenStreamWriter()
            };
            serializers.Serialize(input, writer, input.GetType());
            var reader = new DeserializationContext(this.fixture.SerializationManager)
            {
                StreamReader = new BinaryTokenStreamReader(writer.StreamWriter.ToByteArray())
            };
            var deserialized = (FieldTest)serializers.Deserialize(input.GetType(), reader);

            Assert.Null(input.Context);
            Assert.NotNull(deserialized.Context);
            Assert.Equal(this.fixture.SerializationManager, deserialized.Context.SerializationManager);
        }

        /// <summary>
        /// Tests that <see cref="ILSerializerGenerator"/> does not serialize fields marked as [NonSerialized].
        /// </summary>
        [Fact]
        public void ILSerialized_NonSerializedFields()
        {
            var input = new FieldTest
            {
                One = 1,
                Two = 2,
                NonSerializedInt = 1098
            };
            var generator = new ILSerializerGenerator();
            var serializers = generator.GenerateSerializer(input.GetType());
            var writer = new SerializationContext(this.fixture.SerializationManager)
            {
                StreamWriter = new BinaryTokenStreamWriter()
            };
            serializers.Serialize(input, writer, input.GetType());
            var reader = new DeserializationContext(this.fixture.SerializationManager)
            {
                StreamReader = new BinaryTokenStreamReader(writer.StreamWriter.ToByteArray())
            };
            var deserialized = (FieldTest)serializers.Deserialize(input.GetType(), reader);

            Assert.Equal(input.One, deserialized.One);
            Assert.Equal(input.Two, deserialized.Two);
            Assert.NotEqual(input.NonSerializedInt, deserialized.NonSerializedInt);
            Assert.Equal(default(int), deserialized.NonSerializedInt);
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