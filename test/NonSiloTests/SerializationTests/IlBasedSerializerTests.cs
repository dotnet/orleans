namespace UnitTests.Serialization
{
    using System.Diagnostics.CodeAnalysis;

    using Orleans.Serialization;

    using Xunit;

    [TestCategory("BVT"), TestCategory("Serialization")]
    public class IlBasedSerializerTests
    {
        public IlBasedSerializerTests()
        {
            SerializationManager.InitializeForTesting();
        }

        /// <summary>
        /// Tests that <see cref="IlBasedSerializerGenerator"/> supports distinct field selection for serialization
        /// versus copy operations.
        /// </summary>
        [Fact]
        public void IlBasedSerializer_AllowCopiedFieldsToDifferFromSerializedFields()
        {
            var input = new FieldTest
            {
                One = 1,
                Two = 2,
                Three = 3
            };

            var generator = new IlBasedSerializerGenerator();
            var serializers = generator.GenerateSerializer(input.GetType(), f => f.Name != "One", f => f.Name != "Three");
            var copy = (FieldTest)serializers.DeepCopy(input);
            Assert.Equal(1, copy.One);
            Assert.Equal(2, copy.Two);
            Assert.Equal(0, copy.Three);

            var writer = new BinaryTokenStreamWriter();
            serializers.Serialize(input, writer, input.GetType());
            var reader = new BinaryTokenStreamReader(writer.ToByteArray());
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