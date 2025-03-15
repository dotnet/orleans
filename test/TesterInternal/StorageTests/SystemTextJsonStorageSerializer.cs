using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;
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
        readonly SystemTextJsonGrainStorageSerializer _systemTextJson;
        readonly JsonGrainStorageSerializer _newtonSoft;
        private readonly InProcessTestCluster _testCluster;

        public SystemTextJsonStorageSerializerTests()
        {
            var builder = new InProcessTestClusterBuilder();
            builder.ConfigureSilo((_, builder) =>
            {
                builder.AddMemoryGrainStorage("test");
                builder.AddMemoryStreams("test");

#pragma warning disable ORLEANSEXP004 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
                builder.UseSystemTextJsonGrainStorageSerializer();
#pragma warning restore ORLEANSEXP004 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
            });
            _testCluster = builder.Build();

            _testCluster.DeployAsync().Wait();

            _systemTextJson = (SystemTextJsonGrainStorageSerializer)_testCluster.Silos.First().ServiceProvider.GetRequiredService<IGrainStorageSerializer>();
            _newtonSoft = ActivatorUtilities.CreateInstance<JsonGrainStorageSerializer>(_testCluster.Silos.First().ServiceProvider);
        }

        private void Roundtrip<X>(X instance)
        {
            var ns = _newtonSoft.Serialize(instance);

            _systemTextJson.Deserialize<X>(_systemTextJson.Serialize(instance)).Should().BeEquivalentTo(instance);
            _newtonSoft.Deserialize<X>(ns).Should().BeEquivalentTo(instance);
            
            _systemTextJson.Deserialize<X>(_newtonSoft.Serialize(instance)).Should().BeEquivalentTo(instance);
            _newtonSoft.Deserialize<X>(_systemTextJson.Serialize(instance)).Should().BeEquivalentTo(instance);

            _systemTextJson.Deserialize<X>(_systemTextJson.Serialize(default(X))).Should().BeEquivalentTo(default(X));

            // Looks like the Newtonsoft converts do not handle default or null values

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

        [Fact]
        public async Task AsyncStreamReferenceConverterTest()
        {
            var streamProvider = _testCluster.Silos.First().ServiceProvider.GetRequiredKeyedService<IStreamProvider>("test");


            var stream = streamProvider.GetStream<int>(StreamId.Create("Test_namespace", "Test_key"));

            ConcurrentBag<int> values = new();
            var handle = await stream.SubscribeAsync((x, t) =>
            {
                values.Add(x);
                return Task.CompletedTask;
            });

            stream = _systemTextJson.Deserialize<IAsyncStream<int>>(_systemTextJson.Serialize(stream));

            await stream.OnNextAsync(11);

            stream = _newtonSoft.Deserialize<IAsyncStream<int>>(_systemTextJson.Serialize(stream));

            await stream.OnNextAsync(22);

            stream = _systemTextJson.Deserialize<IAsyncStream<int>>(_newtonSoft.Serialize(stream));

            await stream.OnNextAsync(33);

            stream = _newtonSoft.Deserialize<IAsyncStream<int>>(_newtonSoft.Serialize(stream));

            await stream.OnNextAsync(44);
        
            values.Should().Contain(11);
            values.Should().Contain(22);
            values.Should().Contain(33);
            values.Should().Contain(44);

            await handle.UnsubscribeAsync();
        }

        class Observer : IAsyncObserver<int>
        {
            public Task OnErrorAsync(Exception ex) => throw new NotImplementedException();
            public Task OnNextAsync(int item, StreamSequenceToken token = null) => throw new NotImplementedException();
        }
    }
}
