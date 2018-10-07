using Orleans.Serialization;
using Orleans.Streams;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace UnitTests.OrleansRuntime.Streams
{


    public class StreamIdTests
    {

        private readonly ISiloHost silo;
        public StreamIdTests()
        {

        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void StreamIdSerializationTests()
        {
            var guid = new Guid("{71C5C809-CA45-44C6-9006-BA759F1BAC06}");
            var streamNameSpace = "testNameSpace";
            var providerName = "testProviderName";
            var streamId = StreamId.GetStreamId(guid, providerName, streamNameSpace);
            var uniformHash = streamId.GetUniformHashCode();

            var deserializedStreamId = streamId;
            Assert.Equal(guid, deserializedStreamId.Guid);
            Assert.Equal(streamNameSpace, deserializedStreamId.Namespace);
            Assert.Equal(providerName, deserializedStreamId.ProviderName);
            Assert.Equal(uniformHash, deserializedStreamId.GetUniformHashCode());
        }
    }
}
