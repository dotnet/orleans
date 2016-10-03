using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Serialization;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.SerializerTests
{
    public class MessageSerializerTests
    {
        private readonly ITestOutputHelper output;

        public MessageSerializerTests(ITestOutputHelper output)
        {
            this.output = output;
            MessagingStatisticsGroup.Init(false);

            var orleansConfig = ClusterConfiguration.LocalhostPrimarySilo();
            BufferPool.InitGlobalBufferPool(orleansConfig.Globals);

            SerializationManager.InitializeForTesting();
        }

        [Fact, TestCategory("Functional"), TestCategory("Serialization")]
        public void MessageTest_BinaryRoundTrip()
        {
            RunTest(1000);
        }

        private void RunTest(int numItems)
        {
            InvokeMethodRequest request = new InvokeMethodRequest(0, 0, null);
            Message resp = Message.CreateMessage(request, InvokeMethodOptions.None);
            resp.Id = new CorrelationId();
            resp.SendingSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 200), 0);
            resp.TargetSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 300), 0);
            resp.SendingGrain = GrainId.NewId();
            resp.TargetGrain = GrainId.NewId();
            resp.IsAlwaysInterleave = true;

            List<object> requestBody = new List<object>();
            for (int k = 0; k < numItems; k++)
            {
                requestBody.Add(k + ": test line");
            }

            resp.BodyObject = requestBody;

            string s = resp.ToString();
            output.WriteLine(s);

            int dummy = 0;
            var serialized = resp.Serialize(out dummy);
            int length = serialized.Sum<ArraySegment<byte>>(x => x.Count);
            byte[] data = new byte[length];
            int n = 0;
            foreach (var buffer in serialized)
            {
                Array.Copy(buffer.Array, buffer.Offset, data, n, buffer.Count);
                n += buffer.Count;
            }
            resp.ReleaseBodyAndHeaderBuffers();

            int headerLength = BitConverter.ToInt32(data, 0);
            int bodyLength = BitConverter.ToInt32(data, 4);
            Assert.Equal<int>(length, headerLength + bodyLength + 8); //Serialized lengths are incorrect
            byte[] header = new byte[headerLength];
            Array.Copy(data, 8, header, 0, headerLength);
            byte[] body = new byte[bodyLength];
            Array.Copy(data, 8 + headerLength, body, 0, bodyLength);
            var headerList = new List<ArraySegment<byte>>();
            headerList.Add(new ArraySegment<byte>(header));
            var bodyList = new List<ArraySegment<byte>>();
            bodyList.Add(new ArraySegment<byte>(body));
            var resp1 = new Message(headerList, bodyList);

            //byte[] serialized = resp.FormatForSending();
            //Message resp1 = new Message(serialized, serialized.Length);
            Assert.Equal(resp.Category, resp1.Category); //Category is incorrect"
            Assert.Equal(resp.Direction, resp1.Direction); //Direction is incorrect
            Assert.Equal(resp.Id, resp1.Id); //Correlation ID is incorrect
            Assert.Equal(resp.IsAlwaysInterleave, resp1.IsAlwaysInterleave); //Foo Boolean is incorrect
            Assert.Equal(resp.CacheInvalidationHeader, resp1.CacheInvalidationHeader); //Bar string is incorrect
            Assert.True(resp.TargetSilo.Equals(resp1.TargetSilo));
            Assert.True(resp.TargetGrain.Equals(resp1.TargetGrain));
            Assert.True(resp.SendingGrain.Equals(resp1.SendingGrain));
            Assert.True(resp.SendingSilo.Equals(resp1.SendingSilo)); //SendingSilo is incorrect
            List<object> responseList = Assert.IsAssignableFrom<List<object>>(resp1.BodyObject);
            Assert.Equal<int>(numItems, responseList.Count); //Body list has wrong number of entries
            for (int k = 0; k < numItems; k++)
            {
                Assert.IsAssignableFrom<string>(responseList[k]); //Body list item " + k + " has wrong type
                Assert.Equal<string>((string)(requestBody[k]), (string)(responseList[k])); //Body list item " + k + " is incorrect
            }
        }
    }
}
