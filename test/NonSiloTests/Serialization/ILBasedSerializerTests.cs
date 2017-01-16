using TestExtensions;

namespace UnitTests.Serialization
{
    using System.Diagnostics.CodeAnalysis;

    using Orleans.Serialization;

    using Xunit;

    [TestCategory("BVT"), TestCategory("Serialization")]
    public class ILSerializerTests
    {
        public ILSerializerTests()
        {
            SerializationTestEnvironment.Initialize();
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
            var writer = new SerializationContext
            {
                StreamWriter = new BinaryTokenStreamWriter()
            };
            var copy = (FieldTest)serializers.DeepCopy(input, writer);
            Assert.Equal(1, copy.One);
            Assert.Equal(2, copy.Two);
            Assert.Equal(0, copy.Three);
            
            serializers.Serialize(input, writer, input.GetType());
            var reader = new DeserializationContext
            {
                StreamReader = new BinaryTokenStreamReader(writer.StreamWriter.ToByteArray())
            };
            var deserialized = (FieldTest)serializers.Deserialize(input.GetType(), reader);

            Assert.Equal(0, deserialized.One);
            Assert.Equal(2, deserialized.Two);
            Assert.Equal(3, deserialized.Three);
        }

        [SuppressMessage("ReSharper", "StyleCop.SA1401", Justification = "This is for testing purposes.")]
        private class FieldTest
        {
#pragma warning disable 169
            public int One;
            public int Two;
            public int Three;
#pragma warning restore 169
        }
    }
}