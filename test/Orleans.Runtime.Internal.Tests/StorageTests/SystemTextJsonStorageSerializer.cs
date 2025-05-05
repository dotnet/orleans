using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Providers.StorageSerializer;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime.Hosting;
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
        public ValueTask<int> GetAlt() => ValueTask.FromResult(731131);
    }

    class ReferenceTesterGrain : Grain, IReferenceTesterGrain, IAdditionalInterface
    {
        public ValueTask<Guid> GetId() => ValueTask.FromResult(this.GetPrimaryKey());
    }

    public sealed class SystemTextJsonStorageSerializerTests
    {
        private readonly SystemTextJsonGrainStorageSerializer _systemTextJson;
        private readonly JsonGrainStorageSerializer _newtonSoft;
        private readonly InProcessTestCluster _testCluster;

        public SystemTextJsonStorageSerializerTests()
        {
            var builder = new InProcessTestClusterBuilder();
            builder.ConfigureSilo((_, builder) =>
            {
                builder.AddMemoryGrainStorage("test");
                builder.AddMemoryStreams("test");

#pragma warning disable ORLEANSEXP006 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
                builder.UseSystemTextJsonGrainStorageSerializer();
#pragma warning restore ORLEANSEXP006 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
            });
            _testCluster = builder.Build();

            _testCluster.DeployAsync().Wait();

            _systemTextJson = (SystemTextJsonGrainStorageSerializer)_testCluster.Silos.First().ServiceProvider.GetRequiredService<IGrainStorageSerializer>();
            _newtonSoft = ActivatorUtilities.CreateInstance<JsonGrainStorageSerializer>(_testCluster.Silos.First().ServiceProvider);
        }

        private void Roundtrip<T>(T instance)
        {
            _systemTextJson.Deserialize<T>(_systemTextJson.Serialize(instance)).Should().BeEquivalentTo(instance);
            _newtonSoft.Deserialize<T>(_newtonSoft.Serialize(instance)).Should().BeEquivalentTo(instance);
            
            _systemTextJson.Deserialize<T>(_newtonSoft.Serialize(instance)).Should().BeEquivalentTo(instance);
            _newtonSoft.Deserialize<T>(_systemTextJson.Serialize(instance)).Should().BeEquivalentTo(instance);

            _systemTextJson.Deserialize<T>(_systemTextJson.Serialize(default(T))).Should().BeEquivalentTo(default(T));

            // Looks like the Newtonsoft converts do not handle default or null values, so the below will often fail

            // newtonSoft.Deserialize<X>(newtonSoft.Serialize(default(X))).Should().BeEquivalentTo(default(X));
            // systemTextJson.Deserialize<X>(newtonSoft.Serialize(default(X))).Should().BeEquivalentTo(default(X));
            // newtonSoft.Deserialize<X>(systemTextJson.Serialize(default(X))).Should().BeEquivalentTo(default(X));
        }

        [Fact]
        public void IpAddressV4Converter() => Roundtrip(IPAddress.Parse("127.0.0.1"));

        [Fact]
        public void IpAddressV6Converter() => Roundtrip(IPAddress.Parse("0000:0000:0000:0000:0000:ffff:192.168.100.228"));

        [Fact]
        public void GrainIdConverter() => Roundtrip(new GrainId(GrainType.Create("SomeType"), IdSpan.Create("Id")));

        [Fact]
        public void ActivationIdConverter() => Roundtrip(new ActivationId(Guid.NewGuid()));

        [Fact]
        public void AsyncStreamReferenceConverterTest() => Roundtrip(_testCluster.Silos.First().ServiceProvider.GetRequiredKeyedService<IStreamProvider>("test").GetStream<int>(StreamId.Create("Test_namespace", "Test_key")));

        [Fact]
        public void SiloAddressJsonConverter() => Roundtrip(SiloAddress.New(IPEndPoint.Parse("127.0.0.1:499"), 42));

        [Fact]
        public void MembershipVersionJsonConverter() => Roundtrip(new MembershipVersion(long.MaxValue));

        [Fact]
        public void UniqueKeyConverter() => Roundtrip(UniqueKey.NewKey());

        [Fact]
        public void IpEndPointConverter() => Roundtrip(IPEndPoint.Parse("127.0.0.1:499"));

        [Fact]
        public void EventSequenceTokenV2Converter() => Roundtrip(new EventSequenceTokenV2(35242,24298));

        [Fact]
        public void EventSequenceTokenConverter() => Roundtrip(new EventSequenceToken(2424,1));

        [Fact]
        public void StreamIdConverter() => Roundtrip(StreamId.Create("namespace", "key"));

        [Fact]
        public void StreamIdNullNamespaceConverter() => Roundtrip(StreamId.Create(null, "key"));

        [Fact]
        public void QualifiedStreamIdConverter() => Roundtrip(new QualifiedStreamId("provider", StreamId.Create("namespace", "key")));

        [Fact]
        public void GuidIdRoundtrip() => Roundtrip(GuidId.GetNewGuidId());

        [Fact]
        public void PubSubSubscriptionStateRoundtrip() => new PubSubSubscriptionState(GuidId.GetNewGuidId(), new QualifiedStreamId("test", default), GrainId.Parse("test/test"));

        [Fact]
        public async Task GrainReferenceJsonConverter()
        {
            var grainReference = _testCluster.Client.GetGrain<IReferenceTesterGrain>(Guid.NewGuid());

            await CheckResult(x => x.GetId(), grainReference, _newtonSoft, _newtonSoft);
            await CheckResult(x => x.GetId(), grainReference, _systemTextJson, _systemTextJson);

            await CheckResult(x => x.GetId(), grainReference, _newtonSoft, _systemTextJson);
            await CheckResult(x => x.GetId(), grainReference, _systemTextJson, _newtonSoft);
        }

        [Fact]
        public async Task GrainReferenceJsonConverterAdditionalInterface()
        {
            var grainReference = _testCluster.Client.GetGrain<IReferenceTesterGrain>(Guid.NewGuid()).AsReference<IAdditionalInterface>();

            await CheckResult(x => x.GetAlt(), grainReference, _newtonSoft, _newtonSoft);
            await CheckResult(x => x.GetAlt(), grainReference, _systemTextJson, _systemTextJson);

            await CheckResult(x => x.GetAlt(), grainReference, _newtonSoft, _systemTextJson);
            await CheckResult(x => x.GetAlt(), grainReference, _systemTextJson, _newtonSoft);
        }

        static async Task CheckResult<T, TValue>(Func<T, ValueTask<TValue>> propertyToCheck, T instance, IGrainStorageSerializer serializer, IGrainStorageSerializer deserializer)
        {
            var roundTrippedGrainReference = deserializer.Deserialize<T>(serializer.Serialize(instance));

            var originalValue = await propertyToCheck(instance);
            var newValue = await propertyToCheck(roundTrippedGrainReference);

            originalValue.Should().Be(newValue);
        }
    }
}
