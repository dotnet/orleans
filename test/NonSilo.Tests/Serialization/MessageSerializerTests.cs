using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
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
        private readonly MessageSerializer messageSerializer;

        public MessageSerializerTests(ITestOutputHelper output, TestEnvironmentFixture fixture)
        {
            this.output = output;
            this.fixture = fixture;
            this.messageFactory = this.fixture.Services.GetRequiredService<MessageFactory>();
            this.messageSerializer = this.fixture.Services.GetRequiredService<MessageSerializer>();
        }

        [Fact, TestCategory("Functional")]
        public async Task MessageTest_TtlUpdatedOnAccess()
        {
            var message = this.messageFactory.CreateMessage(null, InvokeMethodOptions.None);

            message.TimeToLive = TimeSpan.FromSeconds(1);
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            Assert.InRange(message.TimeToLive.Value, TimeSpan.FromMilliseconds(-1000), TimeSpan.FromMilliseconds(900));
        }

        [Fact, TestCategory("Functional"), TestCategory("Serialization")]
        public async Task MessageTest_TtlUpdatedOnSerialization()
        {
            var message = this.messageFactory.CreateMessage(null, InvokeMethodOptions.None);

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

                var message = this.messageFactory.CreateMessage(null, InvokeMethodOptions.None);

                var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
                var writer = pipe.Writer;
                Assert.Throws<InvalidMessageFrameException>(() => this.messageSerializer.Write(writer, message));
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
            var request = new[] { arg };
            var message = this.messageFactory.CreateMessage(request, InvokeMethodOptions.None);

            var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
            var writer = pipe.Writer;
            Assert.Throws<InvalidMessageFrameException>(() => this.messageSerializer.Write(writer, message));
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
            BinaryPrimitives.WriteInt32LittleEndian(lengthFields[4..], bodySize);
            writer.Write(lengthFields);
            writer.FlushAsync().AsTask().GetAwaiter().GetResult();

            pipe.Reader.TryRead(out var readResult);
            var reader = readResult.Buffer;
            Assert.Throws<InvalidMessageFrameException>(() => this.messageSerializer.TryRead(ref reader, out var message));
        }

        private Message RoundTripMessage(Message message)
        {
            var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
            var writer = pipe.Writer;
            this.messageSerializer.Write(writer, message);
            writer.FlushAsync().AsTask().GetAwaiter().GetResult();

            pipe.Reader.TryRead(out var readResult);
            var reader = readResult.Buffer;
            var (requiredBytes, _, _) = this.messageSerializer.TryRead(ref reader, out var deserializedMessage);
            Assert.Equal(0, requiredBytes);
            return deserializedMessage;
        }
    }
}
