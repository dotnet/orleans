using System.Threading.Tasks;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.General
{
    using System;
    using Orleans;

    public class InvocationInterceptTests : TestClusterPerTest
    {
        public override TestCluster CreateTestCluster()
        {
            var options = new TestClusterOptions(2);
            options.ClusterConfiguration.AddMemoryStorageProvider("Default");
            options.ClusterConfiguration.AddMemoryStorageProvider("PubSubStore");
            options.ClusterConfiguration.AddSimpleMessageStreamProvider("SMSProvider");
            options.ClientConfiguration.AddSimpleMessageStreamProvider("SMSProvider");
            options.ClusterConfiguration.Globals.RegisterBootstrapProvider<PreInvokeCallbackBootrstrapProvider>(
                "PreInvokeCallbackBootrstrapProvider");
            return new TestCluster(options);
        }

        /// <summary>
        /// Ensures that the invocation interceptor is invoked around method calls.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        [Fact, TestCategory("Functional"), TestCategory("MethodInterception")]
        public async Task PreInvocationCallbackTest()
        {
            var grain = this.GrainFactory.GetGrain<ISimplePersistentGrain>(random.Next());
            
            // This grain method reads the context and returns it
            var context = await grain.GetRequestContext();
            Assert.NotNull(context);
            Assert.True((int)context == 38);
        }
        
        /// <summary>
        /// Ensures that the invocation interceptor is invoked for stream subscribers.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        [Fact, TestCategory("Functional"), TestCategory("MethodInterception")]
        public async Task PreInvocationCallbackWithStreamTest()
        {
            var streamProvider = GrainClient.GetStreamProvider("SMSProvider");
            var id = Guid.NewGuid();
            var stream = streamProvider.GetStream<int>(id, "InterceptedStream");
            var grain = this.GrainFactory.GetGrain<IStreamInterceptionGrain>(id);

            // The intercepted grain should double the value passed to the stream.
            const int TestValue = 43;
            await stream.OnNextAsync(TestValue);
            var actual = await grain.GetLastStreamValue();
            Assert.Equal(TestValue * 2, actual);
        }
    }

    public class PreInvokeCallbackBootrstrapProvider : IBootstrapProvider
    {
        public string Name { get; private set; }

        public Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            providerRuntime.SetInvokeInterceptor((method, request, grain, invoker) =>
            {
                RequestContext.Set("GrainInfo", 38);
                return invoker.Invoke(grain, request);
            });

            return Task.FromResult(0);
        }

        public Task Close()
        {
            return Task.FromResult(0);
        }
    }
}
