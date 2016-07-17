using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;
using Xunit;
using UnitTests.Tester;

namespace UnitTests
{
    public class ClientAddressableTests : HostedTestClusterEnsureDefaultStarted
    {
        private object anchor;

        private class MyPseudoGrain : IClientAddressableTestClientObject
        {
            private int counter = 0;
            private List<int> numbers = new List<int>();

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
                Assert.AreEqual(this.counter, n);
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
                Assert.AreEqual(iterationCount, this.numbers.Count);
                this.numbers.Sort();
                for (var i = 0; i < this.numbers.Count; ++i)
                    Assert.AreEqual(i, this.numbers[i]);
            }
        }

        private class MyProducer : IClientAddressableTestProducer
        {
            int counter = 0;

            public Task<int> Poll()
            {
                ++this.counter;
                return Task.FromResult(this.counter);
            }
        }

        [Fact, TestCategory("ClientAddressable"), TestCategory("Functional")]
        public async Task TestClientAddressableHappyPath()
        {
            var myOb = new MyPseudoGrain();
            this.anchor = myOb;
            var myRef = ((GrainFactory)GrainClient.GrainFactory).CreateObjectReference<IClientAddressableTestClientObject>(myOb);
            var proxy = GrainClient.GrainFactory.GetGrain<IClientAddressableTestGrain>(GetRandomGrainId());
            const string expected = "o hai!";
            await proxy.SetTarget(myRef);
            var actual = await proxy.HappyPath(expected);
            Assert.AreEqual(expected, actual);

            RuntimeClient.Current.DeleteObjectReference(myRef);
        }

        [Fact, TestCategory("ClientAddressable"), TestCategory("Functional")]
        public async Task TestClientAddressableSadPath()
        {
            const string message = "o hai!";

            var myOb = new MyPseudoGrain();
            this.anchor = myOb;
            var myRef = ((GrainFactory)GrainClient.GrainFactory).CreateObjectReference<IClientAddressableTestClientObject>(myOb);
            var proxy = GrainClient.GrainFactory.GetGrain<IClientAddressableTestGrain>(GetRandomGrainId());
            await proxy.SetTarget(myRef);

            await Xunit.Assert.ThrowsAsync<ApplicationException>(() =>
                proxy.SadPath(message)
            );

            RuntimeClient.Current.DeleteObjectReference(myRef);
        }

        [Fact, TestCategory("ClientAddressable"), TestCategory("Functional")]
        public async Task GrainShouldSuccessfullyPullFromClientObject()
        {
            var myOb = new MyProducer();
            this.anchor = myOb;
            var myRef = ((GrainFactory)GrainClient.GrainFactory).CreateObjectReference<IClientAddressableTestProducer>(myOb);
            var rendez = GrainClient.GrainFactory.GetGrain<IClientAddressableTestRendezvousGrain>(0);
            var consumer = GrainClient.GrainFactory.GetGrain<IClientAddressableTestConsumer>(0);

            await rendez.SetProducer(myRef);
            await consumer.Setup();
            var n = await consumer.PollProducer();
            Assert.AreEqual(1, n);

            RuntimeClient.Current.DeleteObjectReference(myRef);
        }

        [Fact, TestCategory("ClientAddressable"), TestCategory("Functional")]
        public async Task MicroClientAddressableSerialStressTest()
        {
            const int iterationCount = 1000;

            var myOb = new MyPseudoGrain();
            this.anchor = myOb;
            var myRef = ((GrainFactory)GrainClient.GrainFactory).CreateObjectReference<IClientAddressableTestClientObject>(myOb);
            var proxy = GrainClient.GrainFactory.GetGrain<IClientAddressableTestGrain>(GetRandomGrainId());
            await proxy.SetTarget(myRef);
            await proxy.MicroSerialStressTest(iterationCount);

            RuntimeClient.Current.DeleteObjectReference(myRef);
        }

        [Fact, TestCategory("ClientAddressable"), TestCategory("Functional")]
        public async Task MicroClientAddressableParallelStressTest()
        {
            const int iterationCount = 1000;

            var myOb = new MyPseudoGrain();
            this.anchor = myOb;
            var myRef = ((GrainFactory)GrainClient.GrainFactory).CreateObjectReference<IClientAddressableTestClientObject>(myOb);
            var proxy = GrainClient.GrainFactory.GetGrain<IClientAddressableTestGrain>(GetRandomGrainId());
            await proxy.SetTarget(myRef);
            await proxy.MicroParallelStressTest(iterationCount);

            RuntimeClient.Current.DeleteObjectReference(myRef);

            myOb.VerifyNumbers(iterationCount);
        }
    }
}
