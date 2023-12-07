using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.CodeGeneration;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;
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
        private readonly SerializerSessionPool _serializerSessionPool;
        private readonly IFieldCodec<GrainAddress> _grainAddressCodec;

        public MessageSerializerTests(ITestOutputHelper output, TestEnvironmentFixture fixture)
        {
            this.output = output;
            this.fixture = fixture;
            this.messageFactory = this.fixture.Services.GetRequiredService<MessageFactory>();
            this.messageSerializer = this.fixture.Services.GetRequiredService<MessageSerializer>();
            _serializerSessionPool = fixture.Services.GetRequiredService<SerializerSessionPool>();
            _grainAddressCodec = fixture.Services.GetRequiredService<IFieldCodec<GrainAddress>>();
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

        [Fact, TestCategory("BVT")]
        public void MessageTest_CacheInvalidationHeader_RoundTripCompatibility()
        {
            var newSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 55555), 55555);

            var oldActivations = new List<GrainAddress>
            {
                GrainAddress.NewActivationAddress(SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 111111), GrainId.Create("test", "1")),
                GrainAddress.NewActivationAddress(SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 22222), 222222), GrainId.Create("test", "2")),
                new() { SiloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 33333), 33333), GrainId = GrainId.Create("test", "3") },
            };

            var newActivations = new List<GrainAddress>
            {
                GrainAddress.NewActivationAddress(newSilo, GrainId.Create("test", "1")),
                GrainAddress.NewActivationAddress(newSilo, GrainId.Create("test", "2")),
                new() { SiloAddress = newSilo, GrainId = GrainId.Create("test", "3") },
            };

            var newUpdates = oldActivations.Zip(newActivations).Select(x => new GrainAddressCacheUpdate(x.First, x.Second)).ToList();

            // Old to new
            {
                using var writer1Session = _serializerSessionPool.GetSession();
                var writer = Writer.CreatePooled(writer1Session);
                var stub = new MessageSerializerBackwardsCompatibilityStub(_grainAddressCodec);
                var fromOld = oldActivations.ToList();
                stub.WriteCacheInvalidationHeaders(ref writer, fromOld);
                writer.Commit();

                using var reader1Session = _serializerSessionPool.GetSession();
                var reader = Reader.Create(writer.Output.AsReadOnlySequence(), reader1Session);
                var toNew = messageSerializer.ReadCacheInvalidationHeaders(ref reader);
                Assert.NotNull(toNew);
                Assert.Equal(fromOld.Count, toNew.Count);
                for (var i = 0; i < fromOld.Count; i++)
                {
                    // Only the invalid grain address can be represented.
                    Assert.Equal(fromOld[i], toNew[i].InvalidGrainAddress);
                    Assert.Null(toNew[i].ValidGrainAddress);
                }

                writer.Dispose();
            }

            // New to new
            {
                using var writer1Session = _serializerSessionPool.GetSession();
                var writer = Writer.CreatePooled(writer1Session);
                var fromNew = newUpdates.ToList();
                messageSerializer.WriteCacheInvalidationHeaders(ref writer, fromNew);
                writer.Commit();

                using var reader1Session = _serializerSessionPool.GetSession();
                var reader = Reader.Create(writer.Output.AsReadOnlySequence(), reader1Session);
                var toNew = messageSerializer.ReadCacheInvalidationHeaders(ref reader);
                Assert.NotNull(toNew);
                Assert.Equal(fromNew.Count, toNew.Count);
                for (var i = 0; i < fromNew.Count; i++)
                {
                    // Full fidelity is expected
                    Assert.Equal(fromNew[i].InvalidGrainAddress, toNew[i].InvalidGrainAddress);
                    Assert.Equal(fromNew[i].ValidGrainAddress, toNew[i].ValidGrainAddress);
                }

                writer.Dispose();
            }

            // New to old
            {
                using var writer1Session = _serializerSessionPool.GetSession();
                var writer = Writer.CreatePooled(writer1Session);
                var fromNew = newUpdates.ToList();
                messageSerializer.WriteCacheInvalidationHeaders(ref writer, fromNew);
                writer.Commit();

                using var reader1Session = _serializerSessionPool.GetSession();
                var reader = Reader.Create(writer.Output.AsReadOnlySequence(), reader1Session);
                var stub = new MessageSerializerBackwardsCompatibilityStub(_grainAddressCodec);
                var toOld = stub.ReadCacheInvalidationHeaders(ref reader);
                Assert.NotNull(toOld);
                Assert.Equal(fromNew.Count, toOld.Count);
                for (var i = 0; i < fromNew.Count; i++)
                {
                    // Only the invalid grain address can be represented.
                    Assert.Equal(fromNew[i].InvalidGrainAddress, toOld[i]);
                }

                writer.Dispose();
            }
        }

        private class MessageSerializerBackwardsCompatibilityStub
        {
            private readonly IFieldCodec<GrainAddress> _grainAddressCodec;

            public MessageSerializerBackwardsCompatibilityStub(IFieldCodec<GrainAddress> grainAddressCodec)
            {
                _grainAddressCodec = grainAddressCodec;
            }
            
            internal List<GrainAddress> ReadCacheInvalidationHeaders<TInput>(ref Reader<TInput> reader)
            {
                var n = (int)reader.ReadVarUInt32();
                if (n > 0)
                {
                    var list = new List<GrainAddress>(n);
                    for (int i = 0; i < n; i++)
                    {
                        list.Add(_grainAddressCodec.ReadValue(ref reader, reader.ReadFieldHeader()));
                    }

                    return list;
                }

                return new List<GrainAddress>();
            }

            internal void WriteCacheInvalidationHeaders<TBufferWriter>(ref Writer<TBufferWriter> writer, List<GrainAddress> value) where TBufferWriter : IBufferWriter<byte>
            {
                writer.WriteVarUInt32((uint)value.Count);
                foreach (var entry in value)
                {
                    _grainAddressCodec.WriteField(ref writer, 0, typeof(GrainAddress), entry);
                }
            }
        }
    }
}
