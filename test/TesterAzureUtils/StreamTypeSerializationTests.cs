using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Formatters.Binary;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Providers.Streams.Common;
using Orleans.Serialization;
using Xunit;
using Orleans.Streams;
using Microsoft.WindowsAzure.Storage.Queue;
using Tester.Serialization;

namespace Tester.AzureUtils.Streaming
{
    public class StreamTypeSerializationTests
    {
        public StreamTypeSerializationTests()
        {
            // FakeSerializer definied in ExternalSerializerTest.cs
            SerializationManager.InitializeForTesting(new List<TypeInfo> { typeof(FakeSerializer).GetTypeInfo() });
            EventSequenceTokenV2.Register();
            AzureQueueBatchContainerV2.Register();
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void AzureQueueBatchContainer_VerifyStillUsingFallbackSerializer()
        {
            var container = new AzureQueueBatchContainer(Guid.NewGuid(), "namespace", new List<object> { "item" }, new Dictionary<string, object>() { { "key", "value" } }, new EventSequenceToken(long.MaxValue, int.MaxValue));
            Tester.SerializationTests.SerializationTestsUtils.VerifyUsingFallbackSerializer(container);
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

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void AzureQueueBatchContainerV2_DeepCopy_IfNotNullAndUsingExternalSerializer()
        {
            var container = CreateAzureQueueBatchContainer();
            var copy = AzureQueueBatchContainerV2.DeepCopy(container, new SerializationContext()) as AzureQueueBatchContainerV2;
            ValidateIdenticalQueueBatchContainerButNotSame(container, copy);
            copy = SerializationManager.DeepCopy(container) as AzureQueueBatchContainerV2;
            ValidateIdenticalQueueBatchContainerButNotSame(container, copy);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void AzureQueueBatchContainerV2_Serialize_IfNotNull()
        {
            var container = CreateAzureQueueBatchContainer();
            var writer = new SerializationContext
            {
                StreamWriter = new BinaryTokenStreamWriter()
            };
            AzureQueueBatchContainerV2.Serialize(container, writer, null);
            var reader = new DeserializationContext
            {
                StreamReader = new BinaryTokenStreamReader(writer.StreamWriter.ToByteArray())
            };

            var deserialized = AzureQueueBatchContainerV2.Deserialize(typeof(AzureQueueBatchContainer), reader) as AzureQueueBatchContainerV2;
            ValidateIdenticalQueueBatchContainerButNotSame(container, deserialized);

            var streamWriter = new BinaryTokenStreamWriter();
            SerializationManager.Serialize(container, streamWriter);
            var streamReader = new BinaryTokenStreamReader(streamWriter.ToByteArray());
            deserialized = SerializationManager.Deserialize<AzureQueueBatchContainerV2>(streamReader);
            ValidateIdenticalQueueBatchContainerButNotSame(container, deserialized);
        }

        private static AzureQueueBatchContainerV2 CreateAzureQueueBatchContainer()
        {
            return new AzureQueueBatchContainerV2(
                Guid.NewGuid(),
                "some namespace",
                new List<object> { new FakeSerialized { SomeData = "text" } },
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
    }
}
