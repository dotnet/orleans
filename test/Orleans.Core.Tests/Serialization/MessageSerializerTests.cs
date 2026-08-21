using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.CodeGeneration;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.Session;
using Orleans.Serialization.TypeSystem;
using Orleans.Serialization.WireProtocol;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.Serialization
{
    /// <summary>
    /// Tests for Orleans message serialization functionality.
    /// </summary>
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    [TestArea("Serialization")]
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
            this.messageSerializer.SetProtocolVersion(NetworkProtocolVersion.Version2);
            _serializerSessionPool = fixture.Services.GetRequiredService<SerializerSessionPool>();
            _grainAddressCodec = fixture.Services.GetRequiredService<IFieldCodec<GrainAddress>>();
        }

        [TestSuite("Functional")]
        [TestProvider("None")]
        [Theory, TestCategory("Functional")]
        [InlineData(10_000)]
        [InlineData(0)]
        public void MessageTest_TtlUpdatedOnAccess(int initialTimeToLiveMilliseconds)
        {
            var message = this.messageFactory.CreateMessage(null, InvokeMethodOptions.None);

            message.TimeToLive = TimeSpan.FromMilliseconds(initialTimeToLiveMilliseconds);
            var expirationTimestamp = message._timeToExpiry.GetRawTimestamp();
            WaitForTimestamp(expirationTimestamp - initialTimeToLiveMilliseconds + 10);

            var accessStarted = CoarseStopwatch.GetTimestamp();
            var timeToLive = message.TimeToLive;
            var accessCompleted = CoarseStopwatch.GetTimestamp();

            Assert.NotNull(timeToLive);
            Assert.True(timeToLive.Value < TimeSpan.FromMilliseconds(initialTimeToLiveMilliseconds));
            Assert.InRange(
                timeToLive.Value,
                TimeSpan.FromMilliseconds(expirationTimestamp - accessCompleted),
                TimeSpan.FromMilliseconds(expirationTimestamp - accessStarted));
        }

        [TestSuite("Functional")]
        [TestProvider("None")]
        [Theory, TestCategory("Functional"), TestCategory("Serialization")]
        [InlineData(10_000)]
        [InlineData(0)]
        public void MessageTest_TtlUpdatedOnSerialization(int initialTimeToLiveMilliseconds)
        {
            var message = this.messageFactory.CreateMessage(null, InvokeMethodOptions.None);

            message.TimeToLive = TimeSpan.FromMilliseconds(initialTimeToLiveMilliseconds);
            var sourceExpirationTimestamp = message._timeToExpiry.GetRawTimestamp();
            WaitForTimestamp(sourceExpirationTimestamp - initialTimeToLiveMilliseconds + 10);
            var deserializedMessage = RoundTripMessage(message, out var serialization, out var deserialization);
            var deserializedExpirationTimestamp = deserializedMessage._timeToExpiry.GetRawTimestamp();

            Assert.NotNull(deserializedMessage.TimeToLive);
            Assert.InRange(
                deserializedExpirationTimestamp,
                sourceExpirationTimestamp - serialization.Completed + deserialization.Started,
                sourceExpirationTimestamp - serialization.Started + deserialization.Completed);
        }

        private static void WaitForTimestamp(long timestamp)
            => Assert.True(SpinWait.SpinUntil(() => CoarseStopwatch.GetTimestamp() >= timestamp, TimeSpan.FromSeconds(1)));

        [TestSuite("Functional")]
        [TestProvider("None")]
        [Fact, TestCategory("Functional"), TestCategory("Serialization")]
        public void Message_SerializeHeaderTooBig()
        {
            try
            {
                // Create a ridiculously big RequestContext
                var maxHeaderSize = this.fixture.Services.GetService<IOptions<SiloMessagingOptions>>()!.Value.MaxMessageHeaderSize;
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

        [TestSuite("Functional")]
        [TestProvider("None")]
        [Fact, TestCategory("Functional"), TestCategory("Serialization")]
        public void Message_SerializeBodyTooBig()
        {
            var maxBodySize = this.fixture.Services.GetService<IOptions<SiloMessagingOptions>>()!.Value.MaxMessageBodySize;

            // Create a request with a ridiculously big argument
            var arg = new byte[maxBodySize + 1];
            var request = new[] { arg };
            var message = this.messageFactory.CreateMessage(request, InvokeMethodOptions.None);

            var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
            var writer = pipe.Writer;
            Assert.Throws<InvalidMessageFrameException>(() => this.messageSerializer.Write(writer, message));
        }

        [TestSuite("Functional")]
        [TestProvider("None")]
        [Fact, TestCategory("Functional"), TestCategory("Serialization")]
        public void Message_DeserializeHeaderTooBig()
        {
            var maxHeaderSize = this.fixture.Services.GetService<IOptions<SiloMessagingOptions>>()!.Value.MaxMessageHeaderSize;
            var maxBodySize = this.fixture.Services.GetService<IOptions<SiloMessagingOptions>>()!.Value.MaxMessageBodySize;

            DeserializeFakeMessage(maxHeaderSize + 1, maxBodySize - 1);
        }

        [TestSuite("Functional")]
        [TestProvider("None")]
        [Fact, TestCategory("Functional"), TestCategory("Serialization")]
        public void Message_DeserializeBodyTooBig()
        {
            var maxHeaderSize = this.fixture.Services.GetService<IOptions<SiloMessagingOptions>>()!.Value.MaxMessageHeaderSize;
            var maxBodySize = this.fixture.Services.GetService<IOptions<SiloMessagingOptions>>()!.Value.MaxMessageBodySize;

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
            return deserializedMessage!;
        }

        private Message RoundTripMessage(
            Message message,
            out (long Started, long Completed) serialization,
            out (long Started, long Completed) deserialization)
        {
            var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
            var writer = pipe.Writer;
            var serializationStarted = CoarseStopwatch.GetTimestamp();
            this.messageSerializer.Write(writer, message);
            var serializationCompleted = CoarseStopwatch.GetTimestamp();
            writer.FlushAsync().AsTask().GetAwaiter().GetResult();

            pipe.Reader.TryRead(out var readResult);
            var reader = readResult.Buffer;
            var deserializationStarted = CoarseStopwatch.GetTimestamp();
            var (requiredBytes, _, _) = this.messageSerializer.TryRead(ref reader, out var deserializedMessage);
            var deserializationCompleted = CoarseStopwatch.GetTimestamp();
            Assert.Equal(0, requiredBytes);
            serialization = (serializationStarted, serializationCompleted);
            deserialization = (deserializationStarted, deserializationCompleted);
            return deserializedMessage!;
        }

        [TestSuite("Functional")]
        [TestProvider("None")]
        [Theory, TestCategory("Functional"), TestCategory("Serialization")]
        [InlineData(0, 0)]
        [InlineData(1, 1)]
        [InlineData(1024, 1024)]
        [InlineData(1025, 1024)]
        [InlineData(2048, 1024)]
        public void Message_RequestContextInitialCapacity_IsBounded(int size, int expected)
        {
            Assert.Equal(expected, MessageSerializer.GetRequestContextInitialCapacity(size));
        }

        [TestSuite("Functional")]
        [TestProvider("None")]
        [Fact, TestCategory("Functional"), TestCategory("Serialization")]
        public void Message_RequestContextBeyondInitialCapacity_RoundTrips()
        {
            const int entryCount = 2048;
            var requestContext = new Dictionary<string, object>(entryCount);
            for (var i = 0; i < entryCount; i++)
            {
                requestContext[$"key-{i}"] = i;
            }

            var message = this.messageFactory.CreateMessage(null, InvokeMethodOptions.None);
            message.RequestContextData = requestContext;

            var deserializedMessage = RoundTripMessage(message);

            Assert.NotNull(deserializedMessage.RequestContextData);
            Assert.Equal(entryCount, deserializedMessage.RequestContextData.Count);
            for (var i = 0; i < entryCount; i++)
            {
                Assert.Equal(i, deserializedMessage.RequestContextData[$"key-{i}"]);
            }
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
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
                var message = new Message { CacheInvalidationHeader = fromNew };
                messageSerializer.WriteCacheInvalidationHeaders(ref writer, message);
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
                var message = new Message { CacheInvalidationHeader = fromNew };
                messageSerializer.WriteCacheInvalidationHeaders(ref writer, message);
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

        [TestSuite("BVT")]
        [TestProvider("None")]
        [Fact, TestCategory("BVT")]
        public void MessageTest_CacheInvalidationHeader_DeserializeCapsHeaderCount()
        {
            var fromNew = new List<GrainAddressCacheUpdate>();
            for (var i = 0; i < Message.MaxCacheInvalidationHeaderEntries + 1; i++)
            {
                var grainId = GrainId.Create("test", i.ToString());
                fromNew.Add(new GrainAddressCacheUpdate(
                    GrainAddress.NewActivationAddress(SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11_000 + i), 11_000 + i), grainId),
                    GrainAddress.NewActivationAddress(SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 22_000 + i), 22_000 + i), grainId)));
            }

            using var writer1Session = _serializerSessionPool.GetSession();
            var writer = Writer.CreatePooled(writer1Session);
            var message = new Message { CacheInvalidationHeader = fromNew };
            messageSerializer.WriteCacheInvalidationHeaders(ref writer, message);
            writer.WriteVarUInt32(42);
            writer.Commit();

            using var reader1Session = _serializerSessionPool.GetSession();
            var reader = Reader.Create(writer.Output.AsReadOnlySequence(), reader1Session);
            var toNew = messageSerializer.ReadCacheInvalidationHeaders(ref reader);
            Assert.NotNull(toNew);
            Assert.Equal(Message.MaxCacheInvalidationHeaderEntries, toNew.Count);
            for (var i = 0; i < Message.MaxCacheInvalidationHeaderEntries; i++)
            {
                Assert.Equal(fromNew[i].InvalidGrainAddress, toNew[i].InvalidGrainAddress);
                Assert.Equal(fromNew[i].ValidGrainAddress, toNew[i].ValidGrainAddress);
            }

            Assert.Equal(42u, reader.ReadVarUInt32());
            writer.Dispose();
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [Fact]
        public void Message_ForwardedBodyKeepsItsMeaningBehindRewrittenHeaders()
        {
            try
            {
                var payload = new GrainAddress { GrainId = GrainId.Create("test", "1"), ActivationId = ActivationId.NewId() };
                RequestContext.Set("payload", payload);

                var sent = this.messageFactory.CreateMessage(new object[] { payload }, InvokeMethodOptions.None);
                var (sentHeader, sentBody) = WriteMessage(sent, NewSerializer());

                using (var session = _serializerSessionPool.GetSession())
                {
                    var reader = Reader.Create(sentBody, session);
                    var standaloneArguments = Assert.IsType<object[]>(this.fixture.Services.GetRequiredService<Serializer<object>>().Deserialize(ref reader));
                    Assert.Equal(payload, Assert.IsType<GrainAddress>(standaloneArguments[0]));
                }

                var received = ReadMessage(sentHeader, sentBody, NewSerializer());

                received.ForwardCount++;
                received.TargetSilo = SiloAddress.New(IPAddress.Loopback, 5555, 1);
                received.AddToCacheInvalidationHeader(
                    new GrainAddress { GrainId = GrainId.Create("test", "1"), ActivationId = ActivationId.NewId() },
                    validAddress: null);

                var (forwardedHeader, _) = WriteMessage(received, NewSerializer());

                var delivered = ReadMessage(forwardedHeader, sentBody, NewSerializer());

                var arguments = Assert.IsType<object[]>(delivered.BodyObject);
                Assert.Equal(payload, Assert.IsType<GrainAddress>(arguments[0]));
            }
            finally
            {
                RequestContext.Clear();
            }
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [Fact]
        public void Message_UnresolvableInvokableAlias_IsPreservedVerbatim()
        {
            var grainReference = RuntimeTypeNameFormatter.Format(typeof(GrainReference));
            var alias = $"(\"inv\",[{grainReference}],[{grainReference}],\"DEADBEEF\")";

            var body = EncodeBodyWithFieldType(alias);
            var message = ReadMessageWithBody(body);

            Assert.NotNull(message);
            var undecoded = Assert.IsType<UndecodedRequestBody>(message.BodyObject);
            Assert.Contains("DEADBEEF", undecoded.Alias);

            var (_, rewritten) = WriteMessage(message);
            Assert.Equal(body, rewritten);
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [Fact]
        public void Message_Version1_BodyStillNamesATypeFromTheHeaders()
        {
            try
            {
                var payload = new GrainAddress { GrainId = GrainId.Create("test", "1"), ActivationId = ActivationId.NewId() };
                RequestContext.Set("payload", payload);

                var message = this.messageFactory.CreateMessage(new object[] { payload }, InvokeMethodOptions.None);
                var (_, body) = WriteMessage(message, Version1Serializer());

                Assert.Throws<UnknownReferencedTypeException>(() =>
                {
                    using var session = _serializerSessionPool.GetSession();
                    var reader = Reader.Create(body, session);
                    _ = this.fixture.Services.GetRequiredService<Serializer<object>>().Deserialize(ref reader);
                });
            }
            finally
            {
                RequestContext.Clear();
            }
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [Fact]
        public void Message_Version1_UnresolvableInvokableAlias_StillThrows()
        {
            var grainReference = RuntimeTypeNameFormatter.Format(typeof(GrainReference));
            var alias = $"(\"inv\",[{grainReference}],[{grainReference}],\"DEADBEEF\")";

            var body = EncodeBodyWithFieldType(alias);
            var exception = Assert.Throws<TypeLoadException>(() => ReadMessageWithBody(body, Version1Serializer()));
            Assert.IsNotType<UnresolvedInvokableAliasException>(exception);
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [Fact]
        public void Message_UnresolvableNonInvokableAlias_StillThrows()
        {
            var grainReference = RuntimeTypeNameFormatter.Format(typeof(GrainReference));
            var alias = $"(\"notaninvokable\",[{grainReference}],\"DEADBEEF\")";

            var body = EncodeBodyWithFieldType(alias);
            Assert.ThrowsAny<TypeLoadException>(() => ReadMessageWithBody(body));
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [Fact]
        public void InvokableAlias_UnresolvableWhereMethodIsMissing_ThrowsTypedException()
        {
            var invokable = typeof(IEchoGrain).Assembly.GetTypes()
                .First(t => t.GetCustomAttribute<Orleans.CompoundTypeAliasAttribute>() is { Components: ["inv", ..] } a && a.Components.Contains(typeof(IEchoGrain)));

            var aliasAttribute = invokable.GetCustomAttribute<Orleans.CompoundTypeAliasAttribute>();
            Assert.NotNull(aliasAttribute);

            var realAlias = RuntimeTypeNameFormatter.Format(invokable);
            var methodHash = (string)aliasAttribute.Components[^1];

            Assert.Equal(invokable, this.fixture.Services.GetRequiredService<TypeConverter>().Parse(realAlias));

            var ex = Assert.Throws<UnresolvedInvokableAliasException>(() => CreateConverterWithoutInvokables().Parse(realAlias));
            Assert.Contains(methodHash, ex.Alias);

            TypeConverter CreateConverterWithoutInvokables() =>
                new(
                    [],
                    [],
                    [],
                    Options.Create(new TypeManifestOptions { AllowAllTypes = true }),
                    new CachedTypeResolver());
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [Fact]
        public void Message_UnresolvableInvokableAliasInResponse_StillThrows()
        {
            var grainReference = RuntimeTypeNameFormatter.Format(typeof(GrainReference));
            var alias = $"(\"inv\",[{grainReference}],[{grainReference}],\"DEADBEEF\")";
            var body = EncodeBodyWithFieldType(alias);
            var request = this.messageFactory.CreateMessage(null, InvokeMethodOptions.None);
            var response = this.messageFactory.CreateResponseMessage(request);
            var (header, _) = WriteMessage(response);

            Assert.Throws<UnresolvedInvokableAliasException>(() => ReadMessage(header, body));
        }

        private byte[] EncodeBodyWithFieldType(string typeName)
        {
            using var session = _serializerSessionPool.GetSession();
            var writer = Writer.CreatePooled(session);
            try
            {
                writer.WriteByte((byte)((uint)WireType.TagDelimited | (uint)SchemaType.Encoded));

                var nameBytes = Encoding.UTF8.GetBytes(typeName);
                writer.WriteByte(1);
                writer.WriteInt32(0);
                writer.WriteVarUInt32((uint)nameBytes.Length);
                writer.Write(nameBytes);

                writer.WriteEndObject();
                writer.Commit();
                return writer.Output.AsReadOnlySequence().ToArray();
            }
            finally
            {
                writer.Dispose();
            }
        }

        private Message ReadMessageWithBody(byte[] body, MessageSerializer? serializer = null)
        {
            serializer ??= this.messageSerializer;
            var (header, _) = WriteMessage(this.messageFactory.CreateMessage(null, InvokeMethodOptions.None), serializer);
            return ReadMessage(header, body, serializer);
        }

        private Message ReadMessage(byte[] header, byte[] body, MessageSerializer? serializer = null)
        {
            serializer ??= this.messageSerializer;

            var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
            Span<byte> lengthFields = stackalloc byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(lengthFields, header.Length);
            BinaryPrimitives.WriteInt32LittleEndian(lengthFields[4..], body.Length);
            pipe.Writer.Write(lengthFields);
            pipe.Writer.Write(header);
            pipe.Writer.Write(body);
            pipe.Writer.FlushAsync().AsTask().GetAwaiter().GetResult();

            pipe.Reader.TryRead(out var readResult);
            var buffer = readResult.Buffer;
            var (requiredBytes, _, _) = serializer.TryRead(ref buffer, out var message);
            Assert.Equal(0, requiredBytes);
            return message!;
        }

        private (byte[] Header, byte[] Body) WriteMessage(Message message, MessageSerializer? serializer = null)
        {
            var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
            var (headerLength, bodyLength) = (serializer ?? this.messageSerializer).Write(pipe.Writer, message);
            pipe.Writer.FlushAsync().AsTask().GetAwaiter().GetResult();

            pipe.Reader.TryRead(out var readResult);
            var framed = readResult.Buffer.ToArray();

            const int framing = 8;
            return (
                framed[framing..(framing + headerLength)],
                framed[(framing + headerLength)..(framing + headerLength + bodyLength)]);
        }

        private MessageSerializer NewSerializer()
        {
            var serializer = this.fixture.Services.GetRequiredService<MessageSerializer>();
            serializer.SetProtocolVersion(NetworkProtocolVersion.Version2);
            return serializer;
        }

        private MessageSerializer Version1Serializer()
        {
            var serializer = this.fixture.Services.GetRequiredService<MessageSerializer>();
            serializer.SetProtocolVersion(NetworkProtocolVersion.Version1);
            return serializer;
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
                        var address = _grainAddressCodec.ReadValue(ref reader, reader.ReadFieldHeader());
                        Assert.NotNull(address);
                        list.Add(address);
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
