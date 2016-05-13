using System.Threading.Tasks;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using Xunit;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace UnitTests.General
{
    public class InvocationInterceptTests : TestClusterPerTest
    {
        public override TestCluster CreateTestCluster()
        {
            var options = new TestClusterOptions(2);
            options.ClusterConfiguration.AddMemoryStorageProvider("Default");
            options.ClusterConfiguration.Globals.RegisterBootstrapProvider<PreInvokeCallbackBootrstrapProvider>(
                "PreInvokeCallbackBootrstrapProvider");
            return new TestCluster(options);
        }

        /// <summary>
        /// Ensures that the invocation interceptor is invoked around method calls.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        [Fact, TestCategory("Functional")]
        public async Task PreInvocationCallbackTest()
        {
            var grain = GrainFactory.GetGrain<ISimplePersistentGrain>(random.Next());
            
            // This grain method reads the context and returns it
            var context = await grain.GetRequestContext();
            Assert.IsNotNull(context);
            Assert.IsTrue((int)context == 38);
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
