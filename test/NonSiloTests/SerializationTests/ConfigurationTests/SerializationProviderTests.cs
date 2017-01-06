using System;
using System.Linq;
using Orleans.Runtime;
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
            var clientConfig = ClientConfiguration.LoadFromFile("SerializationTests\\ConfigurationTests\\ClientConfigurationForSerializer.xml");
            Assert.Equal(1, clientConfig.SerializationProviders.Count);
            Assert.Equal(typeof(FakeSerializer), clientConfig.SerializationProviders.First());
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationProvider_LoadWithNoProvidersTest()
        {
            var clientConfig = ClientConfiguration.LoadFromFile("SerializationTests\\ConfigurationTests\\ClientConfigurationForSerializer2.xml");
            Assert.Equal(0, clientConfig.SerializationProviders.Count);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationProvider_LoadWithDuplicateProviders()
        {
            var clientConfig = ClientConfiguration.LoadFromFile("SerializationTests\\ConfigurationTests\\ClientConfigurationForSerializer3.xml");
            Assert.Equal(1, clientConfig.SerializationProviders.Count);
            Assert.True(clientConfig.SerializationProviders.Contains(typeof(FakeSerializer)));
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationProvider_LoadWithMultipleProvider1()
        {
            var clientConfig = ClientConfiguration.LoadFromFile("SerializationTests\\ConfigurationTests\\ClientConfigurationForSerializer5.xml");
            Assert.Equal(2, clientConfig.SerializationProviders.Count);
            Assert.True(clientConfig.SerializationProviders.Contains(typeof(FakeSerializer)));
            Assert.True(clientConfig.SerializationProviders.Contains(typeof(FakeSerializer2)));
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationProvider_LoadWithClassNotImplementingInterface()
        {
            Xunit.Assert.Throws(typeof(FormatException), ()=> ClientConfiguration.LoadFromFile("SerializationTests\\ConfigurationTests\\ClientConfigurationForSerializer6.xml"));
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationProvider_LoadWithAbstractClass()
        {
            Xunit.Assert.Throws(typeof(FormatException), () => ClientConfiguration.LoadFromFile("SerializationTests\\ConfigurationTests\\ClientConfigurationForSerializer7.xml"));
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationProvider_LoadWithInvalidConstructorClass()
        {
            Xunit.Assert.Throws(typeof(FormatException), () => ClientConfiguration.LoadFromFile("SerializationTests\\ConfigurationTests\\ClientConfigurationForSerializer8.xml"));
        }
    }

    public class FakeSerializer2 : AbstractFakeSerializer
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

        public void Initialize(Logger logger)
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
