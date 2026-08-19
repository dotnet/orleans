#nullable enable
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Providers.Streams.Common;
using Orleans.Storage;
using Orleans.Streams;
using Orleans.TestingHost;
using Xunit;

namespace UnitTests.StorageTests
{
    interface IReferenceTesterGrain : IGrainWithGuidKey
    {
        ValueTask<Guid> GetId();
    }

    interface IAdditionalInterface : IGrainWithGuidKey
    {
        ValueTask<int> GetAlt();
    }

    class ReferenceTesterGrain : Grain, IReferenceTesterGrain, IAdditionalInterface
    {
        public ValueTask<Guid> GetId() => ValueTask.FromResult(this.GetPrimaryKey());
        public ValueTask<int> GetAlt() => ValueTask.FromResult(731131);
    }

    interface ICustomAsyncStream : IAsyncStream<int>
    {
    }

    public sealed class SystemTextJsonStorageSerializerTests : IDisposable
    {
        private sealed class FieldState
        {
            public int Value;
            public string? Name { get; set; }
        }

        private sealed class DerivedSequenceToken(long sequenceNumber, int eventIndex) : EventSequenceTokenV2(sequenceNumber, eventIndex);

        private readonly SystemTextJsonGrainStorageSerializer _systemTextJson;
        private readonly InProcessTestCluster _testCluster;

        public SystemTextJsonStorageSerializerTests()
        {
            var builder = new InProcessTestClusterBuilder();
            builder.ConfigureSilo((_, builder) =>
            {
                builder.AddMemoryGrainStorage("test");
                builder.AddMemoryStreams("test");

                builder.UseSystemTextJsonGrainStorageSerializer();
            });
            _testCluster = builder.Build();

            _testCluster.DeployAsync().GetAwaiter().GetResult();
            _systemTextJson = (SystemTextJsonGrainStorageSerializer)_testCluster.Silos.First().ServiceProvider.GetRequiredService<IGrainStorageSerializer>();
        }

        public void Dispose() => _testCluster.Dispose();

        private void Roundtrip<T>(T instance, bool supportsDictionaryKey = true) where T : notnull
        {
            AssertEquivalent(instance, _systemTextJson.Deserialize<T>(_systemTextJson.Serialize(instance)));

            if (!supportsDictionaryKey)
            {
                return;
            }

            var dict = new Dictionary<T, T>() { { instance, instance } };
            var deserializedDict = _systemTextJson.Deserialize<Dictionary<T, T>>(_systemTextJson.Serialize(dict));
            Assert.NotNull(deserializedDict);
            Assert.Equal(dict.Count, deserializedDict.Count);
            foreach (var kvp in dict)
            {
                Assert.True(deserializedDict.ContainsKey(kvp.Key), $"Dictionary should contain key {kvp.Key}");
                AssertEquivalent(kvp.Value, deserializedDict[kvp.Key]);
            }
        }

        private void AssertJson<T>(string expected, T instance)
        {
            var serialized = _systemTextJson.Serialize(instance);
            Assert.Equal(expected, serialized.ToString());
            AssertEquivalent(instance, _systemTextJson.Deserialize<T>(serialized));
        }

        private void AssertDictionaryKey<T>(string expected, T instance) where T : notnull
        {
            var serialized = _systemTextJson.Serialize(new Dictionary<T, int> { [instance] = 42 });
            using var document = JsonDocument.Parse(serialized);
            Assert.Equal(expected, Assert.Single(document.RootElement.EnumerateObject()).Name);

            var result = _systemTextJson.Deserialize<Dictionary<T, int>>(serialized);
            Assert.Equal(42, result![instance]);
        }

        private static void AssertEquivalent<T>(T? expected, T? actual)
        {
            if (expected is null)
            {
                Assert.Null(actual);
                return;
            }
            Assert.NotNull(actual);
            Assert.Equal(expected, actual);
        }

        private static void AssertEquivalent(StreamId expected, StreamId actual)
        {
            Assert.Equal(expected.FullKey.ToArray(), actual.FullKey.ToArray());
            Assert.Equal(expected.Namespace.ToArray(), actual.Namespace.ToArray());
            Assert.Equal(expected.Key.ToArray(), actual.Key.ToArray());
        }

        [Fact]
        public void IpAddressV4Converter()
        {
            var address = IPAddress.Parse("127.0.0.1");
            AssertJson(JsonSerializer.Serialize("127.0.0.1"), address);
            AssertDictionaryKey("127.0.0.1", address);
        }

        [Fact]
        public void IpAddressV6Converter()
        {
            var address = IPAddress.Parse("2001:db8::1");
            AssertJson(JsonSerializer.Serialize("2001:db8::1"), address);
            AssertDictionaryKey("2001:db8::1", address);
        }

        [Fact]
        public void GrainIdConverter()
        {
            var grainId = new GrainId(GrainType.Create("SomeType"), IdSpan.Create("Id"));
            AssertJson(JsonSerializer.Serialize("SomeType/Id"), grainId);
            AssertDictionaryKey("SomeType/Id", grainId);
        }

        [Fact]
        public void ActivationIdConverter()
        {
            var id = new ActivationId(Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"));
            AssertJson(JsonSerializer.Serialize("@0f8fad5bd9cb469fa16570867728950e"), id);
            AssertDictionaryKey("@0f8fad5bd9cb469fa16570867728950e", id);
        }

        [Fact]
        public void AsyncStreamReferenceConverterTest()
        {
            var stream = _testCluster.Silos.First().ServiceProvider
                .GetRequiredKeyedService<IStreamProvider>("test")
                .GetStream<int>(StreamId.Create("Test_namespace", "Test_key"));
            AssertJson("""["test",["Test_namespace","Test_key"]]""", stream);
            Assert.Equal(stream.IsRewindable, _systemTextJson.Deserialize<IAsyncStream<int>>(_systemTextJson.Serialize(stream))!.IsRewindable);
        }

        [Fact]
        public void AsyncStreamReferenceConverterRejectsNonGenericType()
        {
            var stream = _testCluster.Silos.First().ServiceProvider.GetRequiredKeyedService<IStreamProvider>("test").GetStream<int>(StreamId.Create("Test_namespace", "Test_key"));
            Assert.Throws<JsonException>(() => _systemTextJson.Deserialize<IAsyncStream>(_systemTextJson.Serialize(stream)));
        }

        [Fact]
        public void AsyncStreamReferenceConverterDoesNotClaimCustomInterfaces()
            => Assert.Throws<JsonException>(() => _systemTextJson.Deserialize<ICustomAsyncStream>(
                BinaryData.FromString("""["test",["Test_namespace","Test_key"]]""")));

        [Fact]
        public void SiloAddressJsonConverter()
        {
            var address = SiloAddress.New(IPEndPoint.Parse("127.0.0.1:499"), 42);
            AssertJson(JsonSerializer.Serialize(address.ToParsableString()), address);
            AssertDictionaryKey(address.ToParsableString(), address);
        }

        [Fact]
        public void MembershipVersionJsonConverter()
        {
            var version = new MembershipVersion(long.MaxValue);
            AssertJson("9223372036854775807", version);
            AssertDictionaryKey("9223372036854775807", version);

            AssertJson("-9223372036854775808", MembershipVersion.MinValue);
            AssertDictionaryKey("-9223372036854775808", MembershipVersion.MinValue);

            var escapedKey = _systemTextJson.Deserialize<Dictionary<MembershipVersion, int>>(
                BinaryData.FromString("""{"\u0031":42}"""));
            Assert.Equal(42, escapedKey![new MembershipVersion(1)]);

            Assert.Throws<JsonException>(() => _systemTextJson.Deserialize<MembershipVersion>(BinaryData.FromString(@"""1""")));
        }

        [Fact]
        public void UniqueKeyConverter()
        {
            var key = UniqueKey.NewKey();
            AssertJson(JsonSerializer.Serialize(key.ToHexString()), key);
            AssertDictionaryKey(key.ToHexString(), key);

            var extendedKey = UniqueKey.NewGrainServiceKey("service ", 1);
            AssertJson(JsonSerializer.Serialize(extendedKey.ToHexString()), extendedKey);
            AssertDictionaryKey(extendedKey.ToHexString(), extendedKey);
        }

        [Fact]
        public void ScalarStringConvertersRejectNonStringTokens()
        {
            Assert.Throws<JsonException>(() => _systemTextJson.Deserialize<IPAddress>(BinaryData.FromString("42")));
            Assert.Throws<JsonException>(() => _systemTextJson.Deserialize<IPEndPoint>(BinaryData.FromString("42")));
            Assert.Throws<JsonException>(() => _systemTextJson.Deserialize<UniqueKey>(BinaryData.FromString("42")));
            Assert.Throws<JsonException>(() => _systemTextJson.Deserialize<GuidId>(BinaryData.FromString("42")));
        }

        [Theory]
        [InlineData("192.168.100.228:443")]
        [InlineData("192.168.100.228:0")]
        [InlineData("[2001:db8::1]:443")]
        [InlineData("[::ffff:192.168.100.228]:443")]
        public void IpEndPointConverter(string value)
        {
            var endpoint = IPEndPoint.Parse(value);
            AssertJson(JsonSerializer.Serialize(endpoint.ToString()), endpoint);
            AssertDictionaryKey(endpoint.ToString(), endpoint);
        }

        [Fact]
        public void EventSequenceTokenV2Converter()
            => AssertJson("""[2,35242,24298]""", new EventSequenceTokenV2(35242, 24298));

        [Fact]
        public void EventSequenceTokenConverter()
            => AssertJson("""[1,2424,1]""", new EventSequenceToken(2424, 1));

        [Fact]
        public void EventSequenceTokenConverterOmitsDefaultIndex()
            => AssertJson("""[1,2424]""", new EventSequenceToken(2424));

        [Fact]
        public void EventSequenceTokenConverterRejectsNonNumericEventIndex()
            => Assert.Throws<JsonException>(() => _systemTextJson.Deserialize<StreamSequenceToken>(
                BinaryData.FromString("""[1,2424,"invalid"]""")));

        [Fact]
        public void EventSequenceTokenBaseTypeConverter()
        {
            Roundtrip<StreamSequenceToken>(new EventSequenceToken(2424, 1), supportsDictionaryKey: false);
            Roundtrip<StreamSequenceToken>(new EventSequenceTokenV2(35242, 24298), supportsDictionaryKey: false);
        }

        [Fact]
        public void DerivedEventSequenceTokenFailsExplicitly()
        {
            StreamSequenceToken token = new DerivedSequenceToken(35242, 24298);
            Assert.Throws<NotSupportedException>(() => _systemTextJson.Serialize(token));
        }

        [Fact]
        public void StreamIdConverter()
        {
            var streamId = StreamId.Create("namespace", "key");
            var serialized = _systemTextJson.Serialize(streamId);
            Assert.Equal("""["namespace","key"]""", serialized.ToString());
            AssertEquivalent(streamId, _systemTextJson.Deserialize<StreamId>(serialized));
        }

        [Fact]
        public void StreamIdConverterPreservesUtf8Components()
        {
            var streamId = StreamId.Create("na\u00efve", "key/\u03b2");
            var serialized = _systemTextJson.Serialize(streamId);
            Assert.Equal(JsonSerializer.Serialize(new[] { "na\u00efve", "key/\u03b2" }), serialized.ToString());
            AssertEquivalent(streamId, _systemTextJson.Deserialize<StreamId>(serialized));
        }

        [Fact]
        public void StreamIdConverterRejectsInvalidUtf8()
            => Assert.Throws<JsonException>(() => _systemTextJson.Serialize(
                StreamId.Create(new byte[] { 0, 255 }, new byte[] { 1, 2 })));

        [Fact]
        public void StreamIdConverterRejectsEmptyKey()
        {
            var streamId = StreamId.Create("namespace", "");
            Assert.Throws<JsonException>(() => _systemTextJson.Serialize(streamId));
            Assert.Throws<JsonException>(() => _systemTextJson.Serialize(new Dictionary<StreamId, int> { [streamId] = 42 }));
        }

        [Fact]
        public void StreamIdConverterRejectsInvalidStringBoundaries()
            => Assert.Throws<JsonException>(() => _systemTextJson.Deserialize<Dictionary<StreamId, int>>(
                BinaryData.FromString("""{"1:\uD83D\uDE00key":42}""")));

        [Fact]
        public void StreamIdConverterRejectsOversizedNamespace()
        {
            var json = JsonSerializer.Serialize(new[] { new string('a', ushort.MaxValue + 1), "key" });
            Assert.Throws<JsonException>(() => _systemTextJson.Deserialize<StreamId>(BinaryData.FromString(json)));
        }

        [Fact]
        public void StreamIdConverterDictionaryKey()
        {
            var streamId = StreamId.Create("namespace", "key");
            var serialized = _systemTextJson.Serialize(new Dictionary<StreamId, int> { [streamId] = 42 });
            Assert.Equal("""{"9:namespacekey":42}""", serialized.ToString());

            var result = _systemTextJson.Deserialize<Dictionary<StreamId, int>>(serialized);
            var actual = Assert.Single(result!);
            Assert.Equal(42, actual.Value);
            AssertEquivalent(streamId, actual.Key);
        }

        [Fact]
        public void StreamIdConverterDictionaryKeyWithDelimiters()
        {
            var streamId = StreamId.Create("namespace/segment", "key/value");
            var result = _systemTextJson.Deserialize<Dictionary<StreamId, int>>(
                _systemTextJson.Serialize(new Dictionary<StreamId, int> { [streamId] = 42 }));
            AssertEquivalent(streamId, Assert.Single(result!).Key);
        }

        [Fact]
        public void StreamIdNullNamespaceConverter()
        {
            var streamId = StreamId.Create(null, "key");
            var serialized = _systemTextJson.Serialize(streamId);
            Assert.Equal("""[null,"key"]""", serialized.ToString());
            AssertEquivalent(streamId, _systemTextJson.Deserialize<StreamId>(serialized));
        }

        [Fact]
        public void StreamIdNullNamespaceStringFormatIsStable()
        {
            var streamId = StreamId.Create(null!, "key");
            Assert.Equal("null/key", streamId.ToString());
            Assert.Equal("provider/null/key", new QualifiedStreamId("provider", streamId).ToString());
        }

        [Fact]
        public void QualifiedStreamIdConverter()
            => AssertJson(
                """["provider",["namespace","key"]]""",
                new QualifiedStreamId("provider", StreamId.Create("namespace", "key")));

        [Fact]
        public void QualifiedStreamIdConverterDictionaryKey()
        {
            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", "key"));
            var serialized = _systemTextJson.Serialize(new Dictionary<QualifiedStreamId, int> { [streamId] = 42 });
            Assert.Equal("""{"8:provider9:namespacekey":42}""", serialized.ToString());
            Assert.Equal(streamId, Assert.Single(_systemTextJson.Deserialize<Dictionary<QualifiedStreamId, int>>(serialized)!).Key);
        }

        [Fact]
        public void QualifiedStreamIdConverterDictionaryKeyWithDelimiters()
            => Roundtrip(new QualifiedStreamId("provider:n\u00e4me", StreamId.Create("namespace/segment", "key/value")));

        [Fact]
        public void QualifiedStreamIdConverterRejectsDefaultStreamId()
            => Assert.Throws<JsonException>(() => _systemTextJson.Deserialize<QualifiedStreamId>(
                BinaryData.FromString("""["provider",[null,""]]""")));

        [Fact]
        public void GuidIdRoundtrip()
        {
            var id = GuidId.GetGuidId(Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"));
            AssertJson(JsonSerializer.Serialize("0f8fad5bd9cb469fa16570867728950e"), id);
            AssertDictionaryKey("0f8fad5bd9cb469fa16570867728950e", id);
        }

        [Fact]
        public void PublicFieldsRoundtrip()
        {
            var state = new FieldState { Value = 42, Name = "test" };

            var systemTextJsonResult = _systemTextJson.Deserialize<FieldState>(_systemTextJson.Serialize(state));

            Assert.NotNull(systemTextJsonResult);
            Assert.Equal(state.Value, systemTextJsonResult.Value);
            Assert.Equal(state.Name, systemTextJsonResult.Name);
        }

        [Fact]
        public void SerializerUsesCompactJsonByDefault()
            => Assert.DoesNotContain('\n', _systemTextJson.Serialize(new FieldState { Value = 42, Name = "test" }).ToString());

        [Fact]
        public void PubSubSubscriptionStateRoundtrip()
        {
            var state = new PubSubSubscriptionState(
                GuidId.GetGuidId(Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e")),
                new QualifiedStreamId("test", StreamId.Create("namespace", "key")),
                GrainId.Parse("test/test"));

            var serialized = _systemTextJson.Serialize(state);
            using var document = JsonDocument.Parse(serialized);
            var root = document.RootElement;
            Assert.Equal(3, root.EnumerateObject().Count());
            Assert.Equal("0f8fad5bd9cb469fa16570867728950e", root.GetProperty("subscriptionId").GetString());
            Assert.Equal("test", root.GetProperty("stream")[0].GetString());
            Assert.Equal("test/test", root.GetProperty("consumer").GetString());
            Assert.False(root.TryGetProperty("state", out _));

            var result = _systemTextJson.Deserialize<PubSubSubscriptionState>(serialized);
            Assert.NotNull(result);
            Assert.Equal(state.SubscriptionId, result.SubscriptionId);
            Assert.Equal(state.Stream.ProviderName, result.Stream.ProviderName);
            AssertEquivalent(state.Stream.StreamId, result.Stream.StreamId);
            Assert.Equal(state.Consumer, result.Consumer);
            Assert.Equal(state.FilterData, result.FilterData);
            Assert.Equal(state.state, result.state);
        }

        [Fact]
        public void PubSubSubscriptionStateRejectsMissingIdentity()
            => Assert.Throws<JsonException>(() => _systemTextJson.Deserialize<PubSubSubscriptionState>(BinaryData.FromString("{}")));

        [Fact]
        public async Task GrainReferenceJsonConverter()
        {
            var grainReference = _testCluster.Client.GetGrain<IReferenceTesterGrain>(Guid.NewGuid());

            AssertGrainReferenceJson(grainReference);
            await CheckResult(x => x.GetId(), grainReference);
        }

        [Fact]
        public async Task GrainReferenceJsonConverterAdditionalInterface()
        {
            var grainReference = _testCluster.Client.GetGrain<IReferenceTesterGrain>(Guid.NewGuid()).AsReference<IAdditionalInterface>();

            AssertGrainReferenceJson(grainReference);
            await CheckResult(x => x.GetAlt(), grainReference);
        }

        [Fact]
        public void GrainReferenceJsonConverterRejectsMismatchedInterface()
        {
            var grainReference = _testCluster.Client.GetGrain<IReferenceTesterGrain>(Guid.NewGuid()).AsReference<IAdditionalInterface>();
            Assert.Throws<JsonException>(() => _systemTextJson.Deserialize<IReferenceTesterGrain>(_systemTextJson.Serialize(grainReference)));
        }

        private void AssertGrainReferenceJson(IAddressable grainReference)
        {
            var reference = grainReference.AsReference();
            using var document = JsonDocument.Parse(_systemTextJson.Serialize(grainReference));
            var root = document.RootElement;
            Assert.Equal(2, root.GetArrayLength());
            Assert.Equal(reference.GrainId.ToString(), root[0].GetString());
            Assert.Equal(reference.InterfaceType.ToString(), root[1].GetString());
        }

        async Task CheckResult<T, TValue>(Func<T, ValueTask<TValue>> propertyToCheck, T instance)
        {
            var roundTrippedGrainReference = _systemTextJson.Deserialize<T>(_systemTextJson.Serialize(instance));
            Assert.NotNull(roundTrippedGrainReference);

            var originalValue = await propertyToCheck(instance);
            var newValue = await propertyToCheck(roundTrippedGrainReference);

            Assert.Equal(originalValue, newValue);
        }
    }
}
