using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using Xunit;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace UnitTests.General
{
    using Orleans.Providers;
    
    public class InvocationInterceptTests : HostedTestClusterPerTest
    {
        public override TestingSiloHost CreateSiloHost()
        {
            var host =
                new TestingSiloHost(new TestingSiloOptions
                {
                    StartFreshOrleans = true,
                    AdjustConfig =
                        cfg =>
                            cfg.Globals.RegisterBootstrapProvider<PreInvokeCallbackBootrstrapProvider>(
                                "PreInvokeCallbackBootrstrapProvider")
                });
            return host;
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
