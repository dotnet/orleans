using System;
using System.Linq;
using Orleans.Runtime.Configuration;
using Orleans.Serialization;
using Tester.Serialization;
using Xunit;

namespace UnitTests.Serialization
{
    public class SerializationProviderTests
    {
        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationProvider_LoadWithSingleProviderTest()
        {
            var clientConfig = ClientConfiguration.LoadFromFile("Serialization\\ConfigurationTests\\ClientConfigurationForSerializer.xml");
            Assert.Single(clientConfig.SerializationProviders);
            Assert.Equal(typeof(FakeSerializer), clientConfig.SerializationProviders.First());
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationProvider_LoadWithNoProvidersTest()
        {
            var clientConfig = ClientConfiguration.LoadFromFile("Serialization\\ConfigurationTests\\ClientConfigurationForSerializer2.xml");
            Assert.Empty(clientConfig.SerializationProviders);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationProvider_LoadWithDuplicateProviders()
        {
            var clientConfig = ClientConfiguration.LoadFromFile("Serialization\\ConfigurationTests\\ClientConfigurationForSerializer3.xml");
            Assert.Single(clientConfig.SerializationProviders);
            Assert.Contains(typeof(FakeSerializer), clientConfig.SerializationProviders);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationProvider_LoadWithMultipleProvider1()
        {
            var clientConfig = ClientConfiguration.LoadFromFile("Serialization\\ConfigurationTests\\ClientConfigurationForSerializer5.xml");
            Assert.Equal(2, clientConfig.SerializationProviders.Count);
            Assert.Contains(typeof(FakeSerializer), clientConfig.SerializationProviders);
            Assert.Contains(typeof(FakeExternalSerializer2), clientConfig.SerializationProviders);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationProvider_LoadWithClassNotImplementingInterface()
        {
            Assert.Throws<FormatException>(()=> ClientConfiguration.LoadFromFile("Serialization\\ConfigurationTests\\ClientConfigurationForSerializer6.xml"));
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationProvider_LoadWithAbstractClass()
        {
            Assert.Throws<FormatException>(() => ClientConfiguration.LoadFromFile("Serialization\\ConfigurationTests\\ClientConfigurationForSerializer7.xml"));
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationProvider_LoadWithInvalidConstructorClass()
        {
            Assert.Throws<FormatException>(() => ClientConfiguration.LoadFromFile("Serialization\\ConfigurationTests\\ClientConfigurationForSerializer8.xml"));
        }
    }

    public class FakeExternalSerializer2 : AbstractFakeSerializer
    {
    }

    public class BadConstructor : AbstractFakeSerializer
    {
        internal BadConstructor() : base()
        {
        }
    }

    public abstract class AbstractFakeSerializer : IExternalSerializer
    {
        public static bool Initialized { get; set; }

        public static bool IsSupportedTypeCalled { get; set; }

        public static bool SerializeCalled { get; set; }

        public static bool DeserializeCalled { get; set; }

        public static bool DeepCopyCalled { get; set; }

        public AbstractFakeSerializer()
        {
            Initialized = true;
        }

        public bool IsSupportedType(Type itemType)
        {
            IsSupportedTypeCalled = true;
            return itemType == typeof(FakeSerialized);
        }

        public object DeepCopy(object source, ICopyContext context)
        {
            DeepCopyCalled = true;
            return null;
        }

        public void Serialize(object item, ISerializationContext context, Type expectedType)
        {
            SerializeCalled = true;
            context.StreamWriter.WriteNull();
        }

        public object Deserialize(Type expectedType, IDeserializationContext context)
        {
            DeserializeCalled = true;
            return new FakeSerialized { SomeData = "fake deserialization" };
        }
    }
}
