using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans;

namespace UnitTests.SerializerTests
{
    [TestClass]
    public class MessageSerializerTests
    {
        [TestInitialize]
        public void TestInitialize()
        {
            MessagingStatisticsGroup.Init(false);

            var orleansConfig = new ClusterConfiguration();
            orleansConfig.StandardLoad();
            BufferPool.InitGlobalBufferPool(orleansConfig.Globals);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Serialization")]
        public void MessageTest_BinaryRoundTrip()
        {
            RunTest(1000);
        }

        private static void RunTest(int numItems)
        {
            Message resp = new Message(Message.Categories.Application, Message.Directions.Response);
            resp.Id = new CorrelationId();
            resp.SendingSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 200), 0);
            resp.TargetSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 300), 0);
            resp.SendingGrain = GrainId.NewId();
            resp.TargetGrain = GrainId.NewId();
            resp.SetHeader("Foo", true);
            resp.SetHeader("Bar", "TestBar");
            //resp.SetStringBody("This is test data");

            List<object> requestBody = new List<object>();
            for (int k = 0; k < numItems; k++)
            {
                requestBody.Add(k + ": test line");
            }

            resp.BodyObject = requestBody;

            string s = resp.ToString();
            Console.WriteLine(s);

            var serialized = resp.Serialize();
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
            Assert.AreEqual<int>(length, headerLength + bodyLength + 8, "Serialized lengths are incorrect");
            byte[] header = new byte[headerLength];
            Array.Copy(data, 8, header, 0, headerLength);
            byte[] body = new byte[bodyLength];
            Array.Copy(data, 8 + headerLength, body, 0, bodyLength);
            Message resp1 = new Message(header, body);

            //byte[] serialized = resp.FormatForSending();
            //Message resp1 = new Message(serialized, serialized.Length);
            Assert.AreEqual<Message.Categories>(resp.Category, resp1.Category, "Category is incorrect");
            Assert.AreEqual<Message.Directions>(resp.Direction, resp1.Direction, "Direction is incorrect");
            Assert.AreEqual<CorrelationId>(resp.Id, resp1.Id, "Correlation ID is incorrect");
            Assert.AreEqual<bool>((bool)resp.GetHeader("Foo"), (bool)resp1.GetHeader("Foo"), "Foo Boolean is incorrect");
            Assert.AreEqual<string>((string)resp.GetHeader("Bar"), (string)resp1.GetHeader("Bar"), "Bar string is incorrect");
            Assert.IsTrue(resp.TargetSilo.Equals(resp1.TargetSilo), "TargetSilo is incorrect");
            Assert.IsTrue(resp.SendingSilo.Equals(resp1.SendingSilo), "SendingSilo is incorrect");
            Assert.IsInstanceOfType(resp1.BodyObject, typeof(List<object>), "Body object is wrong type");
            List<object> responseList = resp1.BodyObject as List<object>;
            Assert.AreEqual<int>(numItems, responseList.Count, "Body list has wrong number of entries");
            for (int k = 0; k < numItems; k++)
            {
                Assert.IsInstanceOfType(responseList[k], typeof(string), "Body list item " + k + " has wrong type");
                Assert.AreEqual<string>((string)(requestBody[k]), (string)(responseList[k]), "Body list item " + k + " is incorrect");
            }
        }
    }
}
