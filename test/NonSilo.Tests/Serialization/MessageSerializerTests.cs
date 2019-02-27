using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGeneration;
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

        [Fact, TestCategory("Functional"), TestCategory("Serialization")]
        public async Task MessageTest_TtlUpdatedOnAccess()
        {
            var request = new InvokeMethodRequest(0, 0, 0, null);
            var message = this.messageFactory.CreateMessage(request, InvokeMethodOptions.None);

            message.TimeToLive = TimeSpan.FromSeconds(1);
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            Assert.InRange(message.TimeToLive.Value, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(500));
        }

        [Fact(Skip = "See https://github.com/dotnet/orleans/issues/5718"), TestCategory("Functional"), TestCategory("Serialization")]
        public async Task MessageTest_TtlUpdatedOnSerialization()
        {
            var request = new InvokeMethodRequest(0, 0, 0, null);
            var message = this.messageFactory.CreateMessage(request, InvokeMethodOptions.None);

            message.TimeToLive = TimeSpan.FromSeconds(1);
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            var deserializedMessage = RoundTripMessage(message);

            Assert.NotNull(deserializedMessage.TimeToLive);
            Assert.InRange(deserializedMessage.TimeToLive.Value, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(500));
        }

        private Message RoundTripMessage(Message message)
        {
            var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
            var writer = pipe.Writer;
            this.messageSerializer.Write(ref writer, message);
            writer.FlushAsync().AsTask().GetAwaiter().GetResult();

            pipe.Reader.TryRead(out var readResult);
            var reader = readResult.Buffer;
            Assert.Equal(0, this.messageSerializer.TryRead(ref reader, out var deserializedMessage));
            return deserializedMessage;
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
