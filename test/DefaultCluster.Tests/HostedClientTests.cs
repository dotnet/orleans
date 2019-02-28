using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Concurrency;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using Tester.CodeGenTests;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.General
{
    [TestCategory("BVT"), TestCategory("HostedClient")]
    public class HostedClientTests : IClassFixture<HostedClientTests.Fixture>
    {
        private readonly TimeSpan timeout = Debugger.IsAttached ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(10);
        private readonly ISiloHost silo;

        public class Fixture : IDisposable
        {
            public ISiloHost Silo { get; }

            public Fixture()
            {
                var (siloPort, gatewayPort) = TestClusterNetworkHelper.GetRandomAvailableServerPorts();
                this.Silo = new SiloHostBuilder()
                    .UseLocalhostClustering(siloPort, gatewayPort)
                    .Configure<ClusterOptions>(options =>
                    {
                        options.ClusterId = Guid.NewGuid().ToString();
                        options.ServiceId = Guid.NewGuid().ToString();
                    })
                    .AddMemoryGrainStorage("PubSubStore")
                    .AddMemoryStreams<DefaultMemoryMessageBodySerializer>("MemStream")
                    .Build();
                this.Silo.StartAsync().GetAwaiter().GetResult();
            }

            public void Dispose()
            {
                this.Silo?.Dispose();
            }
        }

        public HostedClientTests(Fixture fixture)
        {
            this.silo = fixture.Silo;
        }

        [Fact]
        public async Task HostedClient_GrainCallTest()
        {
            var client = this.silo.Services.GetRequiredService<IClusterClient>();

            var grain = client.GetGrain<ISimpleGrain>(65);
            await grain.SetA(23);
            var val = await grain.GetA();
            Assert.Equal(23, val);
        }

        [Fact]
        public async Task HostedClient_ReferenceEquality_GrainCallTest()
        {
            var client = this.silo.Services.GetRequiredService<IClusterClient>();
            var grain = client.GetGrain<IGrainWithGenericMethods>(Guid.NewGuid());

            // Strings are immutable.
            object expected = new string('*', 5);
            var actual = await grain.RoundTrip(expected);
            Assert.Same(expected, actual);

            // Grain references are immutable.
            actual = await grain.RoundTrip(grain);
            Assert.Same(grain, actual);

            // Arrays are not immutable, so a copy is expected.
            var collection = new int[] { 1, 3, 9 };
            actual = await grain.RoundTrip(collection);
            Assert.NotSame(expected, actual);

            // Immutable<T> should round-trip without any copying.
            expected = new Immutable<int[]>(collection);
            actual = await grain.RoundTrip(expected);
            Assert.Same(expected, actual);
        }

        [Fact]
        public async Task HostedClient_ObserverTest()
        {
            var client = this.silo.Services.GetRequiredService<IClusterClient>();

            var handle = new AsyncResultHandle();

            var callbackCounter = new int[1];
            var callbacksRecieved = new bool[2];

            var grain = client.GetGrain<ISimpleObserverableGrain>(0);
            var observer = new ObserverTests.SimpleGrainObserver(
                (a, b, result) =>
                {
                    Assert.Null(RuntimeContext.Current);
                    callbackCounter[0]++;

                    if (a == 3 && b == 0)
                        callbacksRecieved[0] = true;
                    else if (a == 3 && b == 2)
                        callbacksRecieved[1] = true;
                    else
                        throw new ArgumentOutOfRangeException("Unexpected callback with values: a=" + a + ",b=" + b);

                    if (callbackCounter[0] == 1)
                    {
                        // Allow for callbacks occurring in any order
                        Assert.True(callbacksRecieved[0] || callbacksRecieved[1]);
                    }
                    else if (callbackCounter[0] == 2)
                    {
                        Assert.True(callbacksRecieved[0] && callbacksRecieved[1]);
                        result.Done = true;
                    }
                    else
                    {
                        Assert.True(false);
                    }
                },
                handle,
                client.ServiceProvider.GetRequiredService<ILogger<ISimpleGrainObserver>>());
            var reference = await client.CreateObjectReference<ISimpleGrainObserver>(observer);
            await grain.Subscribe(reference);
            await grain.SetA(3);
            await grain.SetB(2);

            Assert.True(await handle.WaitForFinished(timeout));

            await client.DeleteObjectReference<ISimpleGrainObserver>(reference);
            Assert.NotNull(observer);
        }

        [Fact]
        public async Task HostedClient_StreamTest()
        {
            var client = this.silo.Services.GetRequiredService<IClusterClient>();

            var handle = new AsyncResultHandle();
            var vals = new List<int>();
            await client.GetStreamProvider("MemStream").GetStream<int>(Guid.Empty, "hi")
                        .SubscribeAsync(
                            (val, token) =>
                            {
                                vals.Add(val);
                                if (vals.Count >= 2) handle.Done = true;
                                return Task.CompletedTask;
                            });
            var stream = client.GetStreamProvider("MemStream").GetStream<int>(Guid.Empty, "hi");
            await stream.OnNextAsync(1);
            await stream.OnNextAsync(409);
            Assert.True(await handle.WaitForFinished(timeout));
            Assert.Equal(new[] { 1, 409 }, vals);
        }
    }
}
