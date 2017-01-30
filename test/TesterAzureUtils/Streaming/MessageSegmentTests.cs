using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.WindowsAzure.Storage.Queue;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime.Host.Providers.Streams.AzureQueue;
using Orleans.Serialization;
using Xunit;

namespace Tester.AzureUtils.Streaming
{
    public class MessageSegmentTests
    {
        [Fact, TestCategory("Functional"), TestCategory("Azure"), TestCategory("Streaming"), TestCategory("Serialization")]
        public void Test_MessageSegment_SerializationAndDeserialization()
        {
            SerializationManager.InitializeForTesting();
            EventSequenceTokenV2.Register();
            AzureQueueBatchContainerV2.Register();
            var size = CloudQueueMessage.MaxMessageSize * 13;
            var buffer = new byte[size];
            var random = new Random();
            random.NextBytes(buffer);
            var container = new AzureQueueBatchContainerV2(Guid.NewGuid(), "namespace", new List<object> {buffer}, new Dictionary<string, object> {{"key", "value"}}, new EventSequenceTokenV2(0, 0));
            var writer = new BinaryTokenStreamWriter();
            SerializationManager.Serialize(container, writer);
            var segments = MessageSegment.CreateRange(writer);
            var deserialized = AzureQueueBatchContainer.FromMessageSegments(segments, 0, true);
            
            Assert.Equal(container.StreamGuid, deserialized.StreamGuid);
            Assert.Equal(container.StreamNamespace, deserialized.StreamNamespace);
            Assert.NotNull(deserialized.RequestContext);
            Assert.Equal(container.RequestContext.Count, deserialized.RequestContext.Count);
            foreach (var key in deserialized.RequestContext.Keys)
            {
                Assert.True(deserialized.RequestContext.ContainsKey(key));
                Assert.Equal(container.RequestContext[key], deserialized.RequestContext[key]);
            }

            var list = deserialized.GetEvents<byte[]>().ToList();
            Assert.Equal(1, list.Count);
            var outBuffer = list.First().Item1;
            Assert.True(buffer.SequenceEqual(outBuffer));
        }
    }
}
