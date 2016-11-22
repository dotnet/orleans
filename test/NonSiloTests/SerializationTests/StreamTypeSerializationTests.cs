using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Formatters.Binary;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Providers.Streams.Common;
using Orleans.Serialization;
using Orleans.ServiceBus.Providers;
using OrleansServiceBus.Providers.Streams.EventHub;
using Xunit;
using Orleans.Streams;
using Microsoft.WindowsAzure.Storage.Queue;

namespace UnitTests.Serialization
{
    public class StreamTypeSerializationTests
    {
        public StreamTypeSerializationTests()
        {
            // FakeSerializer definied in ExternalSerializerTest.cs
            SerializationManager.InitializeForTesting(new List<TypeInfo> { typeof(FakeSerializer).GetTypeInfo() });
            EventSequenceTokenV2.Register();
            EventHubSequenceTokenV2.Register();
            AzureQueueBatchContainerV2.Register();
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void EventSequenceToken_VerifyStillUsingFallbackSerializer()
        {
            var token = new EventSequenceToken(long.MaxValue, int.MaxValue);
            VerifyUsingFallbackSerializer(token);
   
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void AzureQueueBatchContainer_VerifyStillUsingFallbackSerializer()
        {
            var container = new AzureQueueBatchContainer(Guid.NewGuid(), "namespace", new List<object> {"item"}, new Dictionary<string, object>() {{"key", "value"}}, new EventSequenceToken(long.MaxValue, int.MaxValue));
            VerifyUsingFallbackSerializer(container);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void EventHubSequenceToken_VerifyStillUsingFallbackSerializer()
        {
            var token = new EventHubSequenceToken("some offset", long.MaxValue, int.MaxValue);
            VerifyUsingFallbackSerializer(token);
        }


        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void AzureQueueBatchContainer_VerifyBothMessageTypesCanBeDeserialized()
        {
            var container = new AzureQueueBatchContainer(Guid.NewGuid(), "namespace", new List<object> { "item" }, new Dictionary<string, object>() { { "key", "value" } }, new EventSequenceToken(long.MaxValue, int.MaxValue));
            var writer = new BinaryTokenStreamWriter();
            SerializationManager.Serialize(container, writer);
            var bytes = writer.ToByteArray();

            writer = new BinaryTokenStreamWriter();
            var container2 = new AzureQueueBatchContainerV2(Guid.NewGuid(), "namespace", new List<object> { "item" }, new Dictionary<string, object>() { { "key", "value" } }, new EventSequenceTokenV2(long.MaxValue, int.MaxValue));
            SerializationManager.Serialize(container2, writer);
            var bytes2 = writer.ToByteArray();

            var msg = new CloudQueueMessage(bytes);
            var msg2 = new CloudQueueMessage(bytes2);
            var bc1 = (IBatchContainer)AzureQueueBatchContainer.FromCloudQueueMessage(msg, 0);
            var bc2 = (IBatchContainer)AzureQueueBatchContainer.FromCloudQueueMessage(msg2, 0);
            Assert.NotNull(bc1);
            Assert.NotNull(bc2);
        }

        private static void VerifyUsingFallbackSerializer(object ob)
        {
            var writer = new BinaryTokenStreamWriter();
            SerializationManager.FallbackSerializer(ob, writer, ob.GetType());
            var bytes = writer.ToByteArray();

            byte[] defaultFormatterBytes;
            var formatter = new BinaryFormatter();
            using (var stream = new MemoryStream())
            {
                formatter.Serialize(stream, ob);
                stream.Flush();
                defaultFormatterBytes = stream.ToArray();
            }

            var reader = new BinaryTokenStreamReader(bytes);
            var serToken = reader.ReadToken();
            Assert.Equal(SerializationTokenType.Fallback, serToken);
            var length = reader.ReadInt();
            Assert.Equal(length, defaultFormatterBytes.Length);
            var segment = new ArraySegment<byte>(bytes, reader.CurrentPosition, bytes.Length - reader.CurrentPosition);
            Assert.True(segment.SequenceEqual(defaultFormatterBytes));
        }

        #region EventSequenceToken2

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void EventSequenceTokenV2_DeepCopy_IfNotNull()
        {
            var token = new EventSequenceTokenV2(long.MaxValue, int.MaxValue);
            var copy = EventSequenceTokenV2.DeepCopy(token) as EventSequenceToken;
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
            var writer = new BinaryTokenStreamWriter();
            var token = new EventSequenceTokenV2(long.MaxValue, int.MaxValue);
            EventSequenceTokenV2.Serialize(token, writer, null);
            var reader = new BinaryTokenStreamReader(writer.ToByteArray());
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
            var copy = EventHubSequenceTokenV2.DeepCopy(token) as EventSequenceToken;
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
            var writer = new BinaryTokenStreamWriter();
            var token = new EventHubSequenceTokenV2("name", long.MaxValue, int.MaxValue);
            EventHubSequenceTokenV2.Serialize(token, writer, null);
            var reader = new BinaryTokenStreamReader(writer.ToByteArray());
            var deserialized = EventHubSequenceTokenV2.Deserialize(typeof (EventHubSequenceTokenV2), reader) as EventHubSequenceTokenV2;
            Assert.NotNull(deserialized);
            Assert.NotSame(token, deserialized);
            Assert.Equal(token.EventIndex, deserialized.EventIndex);
            Assert.Equal(token.SequenceNumber, deserialized.SequenceNumber);
        }
        #endregion

        #region AzureQueueBatchContainer2

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void AzureQueueBatchContainerV2_DeepCopy_IfNotNullAndUsingExternalSerializer()
        {
            var container = CreateAzureQueueBatchContainer();
            var copy = AzureQueueBatchContainerV2.DeepCopy(container) as AzureQueueBatchContainerV2;
            ValidateIdenticalQueueBatchContainerButNotSame(container, copy);
            copy = SerializationManager.DeepCopy(container) as AzureQueueBatchContainerV2;
            ValidateIdenticalQueueBatchContainerButNotSame(container, copy);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void AzureQueueBatchContainerV2_Serialize_IfNotNull()
        {
            var container = CreateAzureQueueBatchContainer();
            var writer = new BinaryTokenStreamWriter();
            AzureQueueBatchContainerV2.Serialize(container, writer, null);
            var reader = new BinaryTokenStreamReader(writer.ToByteArray());
            var deserialized = AzureQueueBatchContainerV2.Deserialize(typeof (AzureQueueBatchContainer), reader) as AzureQueueBatchContainerV2;
            ValidateIdenticalQueueBatchContainerButNotSame(container, deserialized);

            writer = new BinaryTokenStreamWriter();
            SerializationManager.Serialize(container, writer);
            reader = new BinaryTokenStreamReader(writer.ToByteArray());
            deserialized = SerializationManager.Deserialize<AzureQueueBatchContainerV2>(reader);
            ValidateIdenticalQueueBatchContainerButNotSame(container, deserialized);
        }

        private static AzureQueueBatchContainerV2 CreateAzureQueueBatchContainer()
        {
            return new AzureQueueBatchContainerV2(
                Guid.NewGuid(), 
                "some namespace", 
                new List<object> { new FakeSerialized { SomeData = "text"} }, 
                new Dictionary<string, object>
                {
                    { "some key", new FakeSerialized { SomeData = "text 2" } }
                },
                new EventSequenceTokenV2(long.MaxValue, int.MaxValue));
        }

        private static void ValidateIdenticalQueueBatchContainerButNotSame(AzureQueueBatchContainerV2 orig, AzureQueueBatchContainerV2 copy)
        {
            Assert.NotNull(copy);
            Assert.NotSame(orig, copy);
            Assert.NotNull(copy.SequenceToken);
            Assert.Equal(orig.SequenceToken, copy.SequenceToken);
            Assert.NotSame(orig.SequenceToken, copy.SequenceToken);
            Assert.Equal(orig.StreamGuid, copy.StreamGuid);
            Assert.Equal(orig.StreamNamespace, copy.StreamNamespace);
            Assert.Equal(orig.StreamNamespace, "some namespace");
            Assert.NotNull(copy.RequestContext);
            Assert.NotSame(orig.RequestContext, copy.RequestContext);
            foreach (var kv in orig.RequestContext)
            {
                Assert.True(copy.RequestContext.ContainsKey(kv.Key));
            }

            Assert.True(copy.RequestContext.ContainsKey("some key"));
        }

        #endregion
    }
}
