using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Serialization;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace NonSilo.Tests.UnitTests.SerializerTests
{
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    public class MessageSerializerTests
    {
        private readonly ITestOutputHelper output;
        private readonly TestEnvironmentFixture fixture;
        private readonly MessageFactory messageFactory;

        public MessageSerializerTests(ITestOutputHelper output, TestEnvironmentFixture fixture)
        {
            this.output = output;
            this.fixture = fixture;
            this.messageFactory = this.fixture.Services.GetRequiredService<MessageFactory>();
        }

        [Fact, TestCategory("Functional"), TestCategory("Serialization")]
        public void MessageTest_BinaryRoundTrip()
        {
            RunTest(1000);
        }

        private void RunTest(int numItems)
        {
            InvokeMethodRequest request = new InvokeMethodRequest(0, 2, 0, null);
            Message resp = this.messageFactory.CreateMessage(request, InvokeMethodOptions.None);
            resp.Id = new CorrelationId();
            resp.SendingSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 200), 0);
            resp.TargetSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 300), 0);
            resp.SendingGrain = GrainId.NewId();
            resp.TargetGrain = GrainId.NewId();
            resp.IsAlwaysInterleave = true;
            Assert.True(resp.IsUsingInterfaceVersions);

            List<object> requestBody = new List<object>();
            for (int k = 0; k < numItems; k++)
            {
                requestBody.Add(k + ": test line");
            }

            resp.BodyObject = requestBody;

            string s = resp.ToString();
            output.WriteLine(s);

            int dummy;
            var serialized = resp.Serialize(this.fixture.SerializationManager, out dummy, out dummy);
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
            var context = new DeserializationContext(this.fixture.SerializationManager)
            {
                StreamReader = new BinaryTokenStreamReader(headerList)
            };
            var resp1 = new Message
            {
                Headers = SerializationManager.DeserializeMessageHeaders(context)
            };
            resp1.SetBodyBytes(bodyList);

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
            Assert.True(resp1.IsUsingInterfaceVersions);
            List<object> responseList = Assert.IsAssignableFrom<List<object>>(resp1.GetDeserializedBody(this.fixture.SerializationManager));
            Assert.Equal<int>(numItems, responseList.Count); //Body list has wrong number of entries
            for (int k = 0; k < numItems; k++)
            {
                Assert.IsAssignableFrom<string>(responseList[k]); //Body list item " + k + " has wrong type
                Assert.Equal<string>((string)(requestBody[k]), (string)(responseList[k])); //Body list item " + k + " is incorrect
            }
        }
    }
}
