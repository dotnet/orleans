using System;
using System.Collections.Generic;
using System.Reflection;
using Orleans.Runtime;
using Orleans.Serialization;
using Xunit;

namespace UnitTests.Serialization
{
    [TestCategory("Serialization")]
    public class ExternalSerializerTest
    {
        public ExternalSerializerTest()
        {
            SerializationTestEnvironment.Initialize(new List<TypeInfo> { typeof(FakeSerializer).GetTypeInfo() }, null);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public void SerializationTests_CustomSerializerInitialized()
        {
            Assert.True(FakeSerializer.Initialized, "The fake serializer wasn't discovered");
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public void SerializationTests_CustomSerializerIsSupportedType()
        {
            var data = new FakeSerialized { SomeData = "some data" };
            SerializationManager.RoundTripSerializationForTesting(data);

            Assert.True(FakeSerializer.IsSupportedTypeCalled, "type discovery failed");
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public void SerializationTests_ThatSerializeAndDeserializeWereInvoked()
        {
            var data = new FakeSerialized { SomeData = "some data" };
            SerializationManager.RoundTripSerializationForTesting(data);
            Assert.True(FakeSerializer.SerializeCalled);
            Assert.True(FakeSerializer.DeserializeCalled);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public void SerializationTests_ThatCopyWasInvoked()
        {
            var data = new FakeSerialized { SomeData = "some data" };
            SerializationManager.DeepCopy(data);
            Assert.True(FakeSerializer.DeepCopyCalled);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public void SerializationTests_ExternalSerializerUsedEvenIfCodegenDidntGenerateSerializersForIt()
        {
            var data = new FakeSerializedWithNoCodegenSerializers { SomeData = "some data", SomeMoreData = "more data" };
            SerializationManager.RoundTripSerializationForTesting(data);
            Assert.True(FakeSerializer.SerializeCalled);
            Assert.True(FakeSerializer.DeserializeCalled);
    }

        private class FakeSerializedWithNoCodegenSerializers : FakeSerialized
        {
            public string SomeMoreData;
        }
    }

    public class FakeSerialized
    {
        public string SomeData;
    }

    public class FakeSerializer : IExternalSerializer
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
            return typeof(FakeSerialized).IsAssignableFrom(itemType);
        }

        public object DeepCopy(object source)
        {
            DeepCopyCalled = true;
            return null;
        }

        public void Serialize(object item, BinaryTokenStreamWriter writer, Type expectedType)
        {
            SerializeCalled = true;
            writer.WriteNull();
        }

        public object Deserialize(Type expectedType, BinaryTokenStreamReader reader)
        {
            DeserializeCalled = true;
            reader.ReadToken();
            return (FakeSerialized)Activator.CreateInstance(expectedType);
        }
    }
}
