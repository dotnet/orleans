using Orleans;
using Orleans.Configuration;
using TestExtensions;
using UnitTests.Grains;
using Xunit;

namespace UnitTests.Serialization
{
    public class SerializationOrderTests
    {
        private readonly SerializationTestEnvironment environment;

        public SerializationOrderTests()
        {
            FakeTypeToSerialize.Reset();
            FakeSerializer1.Reset();
            FakeSerializer2.Reset();
            this.environment = SerializationTestEnvironment.InitializeWithDefaults(
                 builder => builder.Configure<SerializationProviderOptions>(
                     options => options.SerializationProviders.AddRange(new[] { typeof(FakeSerializer1), typeof(FakeSerializer2) })));
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void SerializationOrder_VerifyThatExternalIsHigherPriorityThanAttributeDefined()
        {
            FakeSerializer1.SupportedTypes = FakeSerializer2.SupportedTypes = new[] { typeof(FakeTypeToSerialize) };
            var serializationItem = new FakeTypeToSerialize { SomeValue = 1 };
            this.environment.SerializationManager.RoundTripSerializationForTesting(serializationItem);

            Assert.True(
                FakeSerializer1.SerializeCalled,
                "IExternalSerializer.Serialize should have been called on FakeSerializer1");
            Assert.True(
                FakeSerializer1.DeserializeCalled,
                "IExternalSerializer.Deserialize should have been called on FakeSerializer1");
            Assert.False(
                FakeTypeToSerialize.SerializeWasCalled,
                "Serialize on the type should NOT have been called");
            Assert.False(
                FakeTypeToSerialize.DeserializeWasCalled,
                "Deserialize on the type should NOT have been called");
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void SerializationOrder_VerifyThatAttributeDefinedCalledIfNoExternalSerializersSupportType()
        {
            var serializationItem = new FakeTypeToSerialize { SomeValue = 1 };
            FakeSerializer1.SupportedTypes = FakeSerializer2.SupportedTypes = null;
            this.environment.SerializationManager.RoundTripSerializationForTesting(serializationItem);
            Assert.True(FakeTypeToSerialize.SerializeWasCalled, "FakeTypeToSerialize.Serialize should have been called");
            Assert.True(FakeTypeToSerialize.DeserializeWasCalled, "FakeTypeToSerialize.Deserialize should have been called");
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void SerializationOrder_VerifyExternalSerializersInvokedInOrder()
        {
            FakeSerializer1.SupportedTypes = FakeSerializer2.SupportedTypes = new[] { typeof(FakeTypeToSerialize) };
            var serializationItem = new FakeTypeToSerialize { SomeValue = 1 };
            this.environment.SerializationManager.RoundTripSerializationForTesting(serializationItem);
            Assert.True(FakeSerializer1.SerializeCalled, "IExternalSerializer.Serialize should have been called on FakeSerializer1");
            Assert.True(FakeSerializer1.DeserializeCalled, "IExternalSerializer.Deserialize should have been called on FakeSerializer1");
            Assert.False(FakeSerializer2.SerializeCalled, "IExternalSerializer.Serialize should NOT have been called on FakeSerializer2");
            Assert.False(FakeSerializer2.DeserializeCalled, "IExternalSerializer.Deserialize should NOT have been called on FakeSerializer2");
            Assert.False(FakeTypeToSerialize.SerializeWasCalled, "Serialize on the type should NOT have been called");
            Assert.False(FakeTypeToSerialize.DeserializeWasCalled, "Deserialize on the type should NOT have been called");
        }
    }
}
