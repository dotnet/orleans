using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests
{
    public class ClientAddressableTests : HostedTestClusterEnsureDefaultStarted
    {
        private object anchor;
        private readonly IRuntimeClient runtimeClient;

        private class MyPseudoGrain : IClientAddressableTestClientObject
        {
            private int counter = 0;
            private readonly List<int> numbers = new List<int>();

            public Task<string> OnHappyPath(string message)
            {
                if (string.IsNullOrEmpty(message))
                    throw new ArgumentException("target");
                else
                    return Task.FromResult(message);
            }

            public Task OnSadPath(string message)
            {
                if (string.IsNullOrEmpty(message))
                    throw new ArgumentException("target");
                else
                    throw new ApplicationException(message);
            }

            public Task<int> OnSerialStress(int n)
            {
                Assert.Equal(counter, n);
                ++counter;
                return Task.FromResult(10000 + n);
            }

            public Task<int> OnParallelStress(int n)
            {
                numbers.Add(n);
                return Task.FromResult(10000 + n);
            }

            public void VerifyNumbers(int iterationCount)
            {
                Assert.Equal(iterationCount, numbers.Count);
                numbers.Sort();
                for (var i = 0; i < numbers.Count; ++i)
                    Assert.Equal(i, numbers[i]);
            }
        }

        private class MyProducer : IClientAddressableTestProducer
        {
            private int counter = 0;

            public Task<int> Poll()
            {
                ++counter;
                return Task.FromResult(counter);
            }
        }

        public ClientAddressableTests(DefaultClusterFixture fixture) : base(fixture)
        {
            runtimeClient = HostedCluster.ServiceProvider.GetRequiredService<IRuntimeClient>();
        }

        [Fact, TestCategory("BVT"), TestCategory("ClientAddressable")]
        public async Task TestClientAddressableHappyPath()
        {
            var myOb = new MyPseudoGrain();
            anchor = myOb;
            var myRef = ((IInternalGrainFactory)GrainFactory).CreateObjectReference<IClientAddressableTestClientObject>(myOb);
            var proxy = GrainFactory.GetGrain<IClientAddressableTestGrain>(GetRandomGrainId());
            const string expected = "o hai!";
            await proxy.SetTarget(myRef);
            var actual = await proxy.HappyPath(expected);
            Assert.Equal(expected, actual);

            runtimeClient.DeleteObjectReference(myRef);
        }

        [Fact, TestCategory("BVT"), TestCategory("ClientAddressable")]
        public async Task TestClientAddressableSadPath()
        {
            const string message = "o hai!";

            var myOb = new MyPseudoGrain();
            anchor = myOb;
            var myRef = ((IInternalGrainFactory)GrainFactory).CreateObjectReference<IClientAddressableTestClientObject>(myOb);
            var proxy = GrainFactory.GetGrain<IClientAddressableTestGrain>(GetRandomGrainId());
            await proxy.SetTarget(myRef);

            await Assert.ThrowsAsync<ApplicationException>(() =>
                proxy.SadPath(message)
            );

            runtimeClient.DeleteObjectReference(myRef);
        }

        [Fact, TestCategory("BVT"), TestCategory("ClientAddressable")]
        public async Task GrainShouldSuccessfullyPullFromClientObject()
        {
            var myOb = new MyProducer();
            anchor = myOb;
            var myRef = ((IInternalGrainFactory)GrainFactory).CreateObjectReference<IClientAddressableTestProducer>(myOb);
            var rendez = GrainFactory.GetGrain<IClientAddressableTestRendezvousGrain>(0);
            var consumer = GrainFactory.GetGrain<IClientAddressableTestConsumer>(0);

            await rendez.SetProducer(myRef);
            await consumer.Setup();
            var n = await consumer.PollProducer();
            Assert.Equal(1, n);

            runtimeClient.DeleteObjectReference(myRef);
        }

        [Fact, TestCategory("BVT"), TestCategory("ClientAddressable")]
        public async Task MicroClientAddressableSerialStressTest()
        {
            const int iterationCount = 1000;

            var myOb = new MyPseudoGrain();
            anchor = myOb;
            var myRef = ((IInternalGrainFactory)GrainFactory).CreateObjectReference<IClientAddressableTestClientObject>(myOb);
            var proxy = GrainFactory.GetGrain<IClientAddressableTestGrain>(GetRandomGrainId());
            await proxy.SetTarget(myRef);
            await proxy.MicroSerialStressTest(iterationCount);

            runtimeClient.DeleteObjectReference(myRef);
        }

        [Fact, TestCategory("BVT"), TestCategory("ClientAddressable")]
        public async Task MicroClientAddressableParallelStressTest()
        {
            const int iterationCount = 1000;

            var myOb = new MyPseudoGrain();
            anchor = myOb;
            var myRef = ((IInternalGrainFactory)GrainFactory).CreateObjectReference<IClientAddressableTestClientObject>(myOb);
            var proxy = GrainFactory.GetGrain<IClientAddressableTestGrain>(GetRandomGrainId());
            await proxy.SetTarget(myRef);
            await proxy.MicroParallelStressTest(iterationCount);

            runtimeClient.DeleteObjectReference(myRef);

            myOb.VerifyNumbers(iterationCount);
        }
    }
}
