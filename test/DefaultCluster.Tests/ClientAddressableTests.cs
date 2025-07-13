using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests
{
    /// <summary>
    /// Tests client-addressable objects in Orleans, which allow grains to call back to client-side objects.
    /// This feature enables bidirectional communication where grains can invoke methods on objects hosted in the client.
    /// Key scenarios include:
    /// - Observer patterns where grains notify clients of events
    /// - Client-side callbacks for long-running operations
    /// - Polling patterns where grains pull data from clients
    /// Client-addressable objects must be registered with the runtime and have a limited lifetime.
    /// </summary>
    public class ClientAddressableTests : HostedTestClusterEnsureDefaultStarted
    {
        private object anchor;
        private readonly IRuntimeClient runtimeClient;

        /// <summary>
        /// Test implementation of a client-side object that can be called by grains.
        /// Demonstrates various patterns including:
        /// - Successful method execution (OnHappyPath)
        /// - Error propagation (OnSadPath)
        /// - Thread-safe state management (OnSerialStress)
        /// - Concurrent access handling (OnParallelStress)
        /// </summary>
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
                Assert.Equal(this.counter, n);
                ++this.counter;
                return Task.FromResult(10000 + n);
            }

            public Task<int> OnParallelStress(int n)
            {
                this.numbers.Add(n);
                return Task.FromResult(10000 + n);
            }

            public void VerifyNumbers(int iterationCount)
            {
                Assert.Equal(iterationCount, this.numbers.Count);
                this.numbers.Sort();
                for (var i = 0; i < this.numbers.Count; ++i)
                    Assert.Equal(i, this.numbers[i]);
            }
        }

        /// <summary>
        /// Test implementation of a client-side producer that grains can poll for data.
        /// Demonstrates the pull model where grains actively request data from clients.
        /// </summary>
        private class MyProducer : IClientAddressableTestProducer
        {
            private int counter = 0;

            public Task<int> Poll()
            {
                ++this.counter;
                return Task.FromResult(this.counter);
            }
        }

        public ClientAddressableTests(DefaultClusterFixture fixture) : base(fixture)
        {
            this.runtimeClient = this.HostedCluster.ServiceProvider.GetRequiredService<IRuntimeClient>();
        }

        /// <summary>
        /// Tests successful grain-to-client method invocation.
        /// Verifies that:
        /// - Client objects can be registered and referenced by grains
        /// - Grains can successfully invoke methods on client objects
        /// - Return values are properly marshaled back to the grain
        /// - Object references can be properly cleaned up
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("ClientAddressable")]
        public async Task TestClientAddressableHappyPath()
        {
            var myOb = new MyPseudoGrain();
            this.anchor = myOb;
            var myRef = ((IInternalGrainFactory)this.GrainFactory).CreateObjectReference<IClientAddressableTestClientObject>(myOb);
            var proxy = this.GrainFactory.GetGrain<IClientAddressableTestGrain>(GetRandomGrainId());
            const string expected = "o hai!";
            await proxy.SetTarget(myRef);
            var actual = await proxy.HappyPath(expected);
            Assert.Equal(expected, actual);

            this.runtimeClient.DeleteObjectReference(myRef);
        }

        /// <summary>
        /// Tests error propagation from client objects back to grains.
        /// Verifies that exceptions thrown in client-side methods are properly
        /// serialized and re-thrown in the calling grain, maintaining the error message.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("ClientAddressable")]
        public async Task TestClientAddressableSadPath()
        {
            const string message = "o hai!";

            var myOb = new MyPseudoGrain();
            this.anchor = myOb;
            var myRef = ((IInternalGrainFactory)this.GrainFactory).CreateObjectReference<IClientAddressableTestClientObject>(myOb);
            var proxy = this.GrainFactory.GetGrain<IClientAddressableTestGrain>(GetRandomGrainId());
            await proxy.SetTarget(myRef);

            await Assert.ThrowsAsync<ApplicationException>(() =>
                proxy.SadPath(message)
            );

            this.runtimeClient.DeleteObjectReference(myRef);
        }

        /// <summary>
        /// Tests the pull pattern where grains request data from client objects.
        /// Verifies that:
        /// - Client producer objects can be shared between grains via a rendezvous grain
        /// - Consumer grains can pull data from client-side producers
        /// - State is maintained correctly in the client object across calls
        /// This pattern is useful for scenarios where clients generate data that grains consume.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("ClientAddressable")]
        public async Task GrainShouldSuccessfullyPullFromClientObject()
        {
            var myOb = new MyProducer();
            this.anchor = myOb;
            var myRef = ((IInternalGrainFactory)this.GrainFactory).CreateObjectReference<IClientAddressableTestProducer>(myOb);
            var rendez = this.GrainFactory.GetGrain<IClientAddressableTestRendezvousGrain>(0);
            var consumer = this.GrainFactory.GetGrain<IClientAddressableTestConsumer>(0);

            await rendez.SetProducer(myRef);
            await consumer.Setup();
            var n = await consumer.PollProducer();
            Assert.Equal(1, n);

            this.runtimeClient.DeleteObjectReference(myRef);
        }

        /// <summary>
        /// Stress tests serial execution of many client object invocations.
        /// Verifies that:
        /// - Client objects maintain correct state across many sequential calls
        /// - Orleans properly serializes access to client objects
        /// - No race conditions occur in the serial execution model
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("ClientAddressable")]
        public async Task MicroClientAddressableSerialStressTest()
        {
            const int iterationCount = 1000;

            var myOb = new MyPseudoGrain();
            this.anchor = myOb;
            var myRef = ((IInternalGrainFactory)this.GrainFactory).CreateObjectReference<IClientAddressableTestClientObject>(myOb);
            var proxy = this.GrainFactory.GetGrain<IClientAddressableTestGrain>(GetRandomGrainId());
            await proxy.SetTarget(myRef);
            await proxy.MicroSerialStressTest(iterationCount);

            this.runtimeClient.DeleteObjectReference(myRef);
        }

        /// <summary>
        /// Stress tests parallel execution of many client object invocations.
        /// Verifies that:
        /// - Client objects can handle concurrent access from grains
        /// - All parallel invocations are processed without data loss
        /// - Thread-safety is maintained in the client object
        /// This tests Orleans' ability to handle high-throughput client callbacks.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("ClientAddressable")]
        public async Task MicroClientAddressableParallelStressTest()
        {
            const int iterationCount = 1000;

            var myOb = new MyPseudoGrain();
            this.anchor = myOb;
            var myRef = ((IInternalGrainFactory)this.GrainFactory).CreateObjectReference<IClientAddressableTestClientObject>(myOb);
            var proxy = this.GrainFactory.GetGrain<IClientAddressableTestGrain>(GetRandomGrainId());
            await proxy.SetTarget(myRef);
            await proxy.MicroParallelStressTest(iterationCount);

            this.runtimeClient.DeleteObjectReference(myRef);

            myOb.VerifyNumbers(iterationCount);
        }
    }
}
