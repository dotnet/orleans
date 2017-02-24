using System.Collections.Generic;
using System.Reflection;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime.Configuration;
using Orleans.Serialization;
using Orleans.ServiceBus.Providers;
using OrleansServiceBus.Providers.Streams.EventHub;
using Xunit;
using Tester.Serialization;
using TestExtensions;

namespace ServiceBus.Tests.Serialization
{
    public class FakeSerializerFixture
    {
        public SerializationTestEnvironment Environment = SerializationTestEnvironment.InitializeWithDefaults(
            new ClientConfiguration
            {
                SerializationProviders =
                {
                    typeof(FakeSerializer).GetTypeInfo()
                }
            });
    }

    public class StreamTypeSerializationTests : IClassFixture<FakeSerializerFixture>
    {
        private readonly FakeSerializerFixture fixture;

        public StreamTypeSerializationTests(FakeSerializerFixture fixture)
        {
            this.fixture = fixture;
        }

        public SerializationManager SerializationManager => this.fixture.Environment.SerializationManager;

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void EventSequenceToken_VerifyStillUsingFallbackSerializer()
        {
            var token = new EventSequenceToken(long.MaxValue, int.MaxValue);
            Tester.SerializationTests.SerializationTestsUtils.VerifyUsingFallbackSerializer(this.SerializationManager, token);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void EventHubSequenceToken_VerifyStillUsingFallbackSerializer()
        {
            var token = new EventHubSequenceToken("some offset", long.MaxValue, int.MaxValue);
            Tester.SerializationTests.SerializationTestsUtils.VerifyUsingFallbackSerializer(this.SerializationManager, token);
        }

        #region EventSequenceToken2

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void EventSequenceTokenV2_DeepCopy_IfNotNull()
        {
            var token = new EventSequenceTokenV2(long.MaxValue, int.MaxValue);
            var copy = EventSequenceTokenV2.DeepCopy(token, new SerializationContext(this.SerializationManager)) as EventSequenceToken;
            Assert.NotNull(copy);
            Assert.NotSame(token, copy);
            Assert.Equal(token.EventIndex, copy.EventIndex);
            Assert.Equal(token.SequenceNumber, copy.SequenceNumber);

            var writer = new BinaryTokenStreamWriter();
            SerializationManager.Serialize(token, writer);
            var bytes = writer.ToByteArray();

            var reader = new BinaryTokenStreamReader(bytes);
            copy = SerializationManager.Deserialize(reader) as EventSequenceToken;
            Assert.NotNull(copy);
            Assert.NotSame(token, copy);
            Assert.Equal(token.EventIndex, copy.EventIndex);
            Assert.Equal(token.SequenceNumber, copy.SequenceNumber);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void EventSequenceTokenV2_Serialize_IfNotNull()
        {
            var writer = new SerializationContext(this.SerializationManager)
            {
                StreamWriter = new BinaryTokenStreamWriter()
            };
            var token = new EventSequenceTokenV2(long.MaxValue, int.MaxValue);
            EventSequenceTokenV2.Serialize(token, writer, null);
            var reader = new DeserializationContext(this.SerializationManager)
            {
                StreamReader = new BinaryTokenStreamReader(writer.StreamWriter.ToByteArray())
            };

            var deserialized = EventSequenceTokenV2.Deserialize(typeof(EventSequenceTokenV2), reader) as EventSequenceTokenV2;
            Assert.NotNull(deserialized);
            Assert.NotSame(token, deserialized);
            Assert.Equal(token.EventIndex, deserialized.EventIndex);
            Assert.Equal(token.SequenceNumber, deserialized.SequenceNumber);
        }

        #endregion

        #region EventHubSequenceToken2

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void EventHubSequenceTokenV2_DeepCopy_IfNotNull()
        {
            var token = new EventHubSequenceTokenV2("name", long.MaxValue, int.MaxValue);
            var copy = EventHubSequenceTokenV2.DeepCopy(token, new SerializationContext(this.SerializationManager)) as EventSequenceToken;
            Assert.NotNull(copy);
            Assert.NotSame(token, copy);
            Assert.Equal(token.EventIndex, copy.EventIndex);
            Assert.Equal(token.SequenceNumber, copy.SequenceNumber);

            var writer = new BinaryTokenStreamWriter();
            SerializationManager.Serialize(token, writer);
            var bytes = writer.ToByteArray();

            var reader = new BinaryTokenStreamReader(bytes);
            copy = SerializationManager.Deserialize(reader) as EventHubSequenceTokenV2;
            Assert.NotNull(copy);
            Assert.NotSame(token, copy);
            Assert.Equal(token.EventIndex, copy.EventIndex);
            Assert.Equal(token.SequenceNumber, copy.SequenceNumber);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void EventHubSequenceTokenV2_Serialize_IfNotNull()
        {
            var writer = new SerializationContext(this.SerializationManager)
            {
                StreamWriter = new BinaryTokenStreamWriter()
            };

            var token = new EventHubSequenceTokenV2("name", long.MaxValue, int.MaxValue);
            EventHubSequenceTokenV2.Serialize(token, writer, null);
            var reader = new DeserializationContext(this.SerializationManager)
            {
                StreamReader = new BinaryTokenStreamReader(writer.StreamWriter.ToByteArray())
            };
            var deserialized = EventHubSequenceTokenV2.Deserialize(typeof (EventHubSequenceTokenV2), reader) as EventHubSequenceTokenV2;
            Assert.NotNull(deserialized);
            Assert.NotSame(token, deserialized);
            Assert.Equal(token.EventIndex, deserialized.EventIndex);
            Assert.Equal(token.SequenceNumber, deserialized.SequenceNumber);
        }

        #endregion
    }
}
