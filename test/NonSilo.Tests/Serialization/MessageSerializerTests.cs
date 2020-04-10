using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.CodeGeneration;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.Serialization
{
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    public class MessageSerializerTests
    {
        private readonly ITestOutputHelper output;
        private readonly TestEnvironmentFixture fixture;
        private readonly MessageFactory messageFactory;
        private readonly IMessageSerializer messageSerializer;

        public MessageSerializerTests(ITestOutputHelper output, TestEnvironmentFixture fixture)
        {
            this.output = output;
            this.fixture = fixture;
            this.messageFactory = this.fixture.Services.GetRequiredService<MessageFactory>();
            this.messageSerializer = this.fixture.Services.GetRequiredService<IMessageSerializer>();
        }

        [Fact, TestCategory("Functional"), TestCategory("Serialization")]
        public void MessageTest_BinaryRoundTrip()
        {
            RunTest(1000);
        }

        [Fact, TestCategory("Functional")]
        public async Task MessageTest_TtlUpdatedOnAccess()
        {
            var request = new InvokeMethodRequest(0, 0, 0, null);
            var message = this.messageFactory.CreateMessage(request, InvokeMethodOptions.None);

            message.TimeToLive = TimeSpan.FromSeconds(1);
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            Assert.InRange(message.TimeToLive.Value, TimeSpan.FromMilliseconds(-1000), TimeSpan.FromMilliseconds(900));
        }

        [Fact, TestCategory("Functional"), TestCategory("Serialization")]
        public async Task MessageTest_TtlUpdatedOnSerialization()
        {
            var request = new InvokeMethodRequest(0, 0, 0, null);
            var message = this.messageFactory.CreateMessage(request, InvokeMethodOptions.None);

            message.TimeToLive = TimeSpan.FromSeconds(1);
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            var deserializedMessage = RoundTripMessage(message);

            Assert.NotNull(deserializedMessage.TimeToLive);
            Assert.InRange(message.TimeToLive.Value, TimeSpan.FromMilliseconds(-1000), TimeSpan.FromMilliseconds(900));
        }

        [Fact, TestCategory("Functional"), TestCategory("Serialization")]
        public void Message_SerializeHeaderTooBig()
        {
            try
            {
                // Create a ridiculously big RequestContext
                var maxHeaderSize = this.fixture.Services.GetService<IOptions<SiloMessagingOptions>>().Value.MaxMessageHeaderSize;
                RequestContext.Set("big_object", new byte[maxHeaderSize + 1]);

                var request = new InvokeMethodRequest(0, 0, 0, null);
                var message = this.messageFactory.CreateMessage(request, InvokeMethodOptions.None);

                var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
                var writer = pipe.Writer;
                Assert.Throws<OrleansException>(() => this.messageSerializer.Write(ref writer, message));
            }
            finally
            {
                RequestContext.Clear();
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Serialization")]
        public void Message_SerializeBodyTooBig()
        {
            var maxBodySize = this.fixture.Services.GetService<IOptions<SiloMessagingOptions>>().Value.MaxMessageBodySize;

            // Create a request with a ridiculously big argument
            var arg = new byte[maxBodySize + 1];
            var request = new InvokeMethodRequest(0, 0, 0, new[] { arg });
            var message = this.messageFactory.CreateMessage(request, InvokeMethodOptions.None);

            var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
            var writer = pipe.Writer;
            Assert.Throws<OrleansException>(() => this.messageSerializer.Write(ref writer, message));
        }

        [Fact, TestCategory("Functional"), TestCategory("Serialization")]
        public void Message_DeserializeHeaderTooBig()
        {
            var maxHeaderSize = this.fixture.Services.GetService<IOptions<SiloMessagingOptions>>().Value.MaxMessageHeaderSize;
            var maxBodySize = this.fixture.Services.GetService<IOptions<SiloMessagingOptions>>().Value.MaxMessageBodySize;

            DeserializeFakeMessage(maxHeaderSize + 1, maxBodySize - 1);
        }

        [Fact, TestCategory("Functional"), TestCategory("Serialization")]
        public void Message_DeserializeBodyTooBig()
        {
            var maxHeaderSize = this.fixture.Services.GetService<IOptions<SiloMessagingOptions>>().Value.MaxMessageHeaderSize;
            var maxBodySize = this.fixture.Services.GetService<IOptions<SiloMessagingOptions>>().Value.MaxMessageBodySize;

            DeserializeFakeMessage(maxHeaderSize - 1, maxBodySize + 1);
        }

        private void DeserializeFakeMessage(int headerSize, int bodySize)
        {
            var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
            var writer = pipe.Writer;

            Span<byte> lengthFields = stackalloc byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(lengthFields, headerSize);
            BinaryPrimitives.WriteInt32LittleEndian(lengthFields.Slice(4), bodySize);
            writer.Write(lengthFields);
            writer.FlushAsync().AsTask().GetAwaiter().GetResult();

            pipe.Reader.TryRead(out var readResult);
            var reader = readResult.Buffer;
            Assert.Throws<OrleansException>(() => this.messageSerializer.TryRead(ref reader, out var message));
        }

        private Message RoundTripMessage(Message message)
        {
            var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
            var writer = pipe.Writer;
            this.messageSerializer.Write(ref writer, message);
            writer.FlushAsync().AsTask().GetAwaiter().GetResult();

            pipe.Reader.TryRead(out var readResult);
            var reader = readResult.Buffer;
            var (requiredBytes, _, _) = this.messageSerializer.TryRead(ref reader, out var deserializedMessage);
            Assert.Equal(0, requiredBytes);
            return deserializedMessage;
        }

        private void RunTest(int numItems)
        {
            InvokeMethodRequest request = new InvokeMethodRequest(0, 2, 0, null);
            Message resp = this.messageFactory.CreateMessage(request, InvokeMethodOptions.None);
            resp.Id = new CorrelationId();
            resp.SendingSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 200), 0);
            resp.TargetSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 300), 0);
            resp.SendingGrain = LegacyGrainId.NewId();
            resp.TargetGrain = LegacyGrainId.NewId();
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
            
            var resp1 = RoundTripMessage(resp);

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
            List<object> responseList = Assert.IsAssignableFrom<List<object>>(resp1.BodyObject);
            Assert.Equal<int>(numItems, responseList.Count); //Body list has wrong number of entries
            for (int k = 0; k < numItems; k++)
            {
                Assert.IsAssignableFrom<string>(responseList[k]); //Body list item " + k + " has wrong type
                Assert.Equal((string)(requestBody[k]), (string)(responseList[k])); //Body list item " + k + " is incorrect
            }
        }
    }
}
