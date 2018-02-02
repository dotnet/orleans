using System.Reflection;
using Orleans.Runtime.Configuration;
using Tester.Serialization;
using TestExtensions;
using Xunit;

namespace UnitTests.Serialization
{
    [TestCategory("Serialization")]
    public class ExternalSerializerTest
    {
        private readonly SerializationTestEnvironment environment;

        public ExternalSerializerTest()
        {
            var config = new ClientConfiguration
            {
                SerializationProviders =
                {
                    typeof(FakeSerializer).GetTypeInfo()
                }
            };
            this.environment = SerializationTestEnvironment.InitializeWithDefaults(config);
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
            this.environment.SerializationManager.RoundTripSerializationForTesting(data);

            Assert.True(FakeSerializer.IsSupportedTypeCalled, "type discovery failed");
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public void SerializationTests_ThatSerializeAndDeserializeWereInvoked()
        {
            var data = new FakeSerialized { SomeData = "some data" };
            this.environment.SerializationManager.RoundTripSerializationForTesting(data);
            Assert.True(FakeSerializer.SerializeCalled);
            Assert.True(FakeSerializer.DeserializeCalled);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public void SerializationTests_ThatCopyWasInvoked()
        {
            var data = new FakeSerialized { SomeData = "some data" };
            this.environment.SerializationManager.DeepCopy(data);
            Assert.True(FakeSerializer.DeepCopyCalled);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public void SerializationTests_ExternalSerializerUsedEvenIfCodegenDidntGenerateSerializersForIt()
        {
            var data = new FakeSerializedWithNoCodegenSerializers { SomeData = "some data", SomeMoreData = "more data" };
            this.environment.SerializationManager.RoundTripSerializationForTesting(data);
            Assert.True(FakeSerializer.SerializeCalled);
            Assert.True(FakeSerializer.DeserializeCalled);
        }

        private class FakeSerializedWithNoCodegenSerializers : FakeSerialized
        {
            public string SomeMoreData;
        }
    }
}