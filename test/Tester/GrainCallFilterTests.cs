using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
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

    [TestCategory("BVT"), TestCategory("GrainCallFilter")]
    public class GrainCallFilterTests : TestClusterPerTest
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
            options.ClusterConfiguration.UseStartupType<SiloInvokerTestStartup>();
            return new TestCluster(options);
        }

        /// <summary>
        /// Ensures that grain call filters are invoked around method calls in the correct order.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        [Fact]
        public async Task GrainCallFilter_Order_Test()
        {
            var grain = this.GrainFactory.GetGrain<IGrainCallFilterTestGrain>(random.Next());

            // This grain method reads the context and returns it
            var context = await grain.GetRequestContext();
            Assert.NotNull(context);
            Assert.Equal("grain!", context);
        }
        
        /// <summary>
        /// Ensures that the invocation interceptor is invoked for stream subscribers.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        [Fact]
        public async Task GrainCallFilter_Stream_Test()
        {
            var streamProvider = this.Client.GetStreamProvider("SMSProvider");
            var id = Guid.NewGuid();
            var stream = streamProvider.GetStream<int>(id, "InterceptedStream");
            var grain = this.GrainFactory.GetGrain<IStreamInterceptionGrain>(id);

            // The intercepted grain should double the value passed to the stream.
            const int TestValue = 43;
            await stream.OnNextAsync(TestValue);
            var actual = await grain.GetLastStreamValue();
            Assert.Equal(TestValue * 2, actual);
        }

        /// <summary>
        /// Tests that some invalid usages of invoker interceptors are denied.
        /// </summary>
        /// <returns></returns>
        [Fact]
        public async Task GrainCallFilter_InvalidOrder_Test()
        {
            var grain = this.GrainFactory.GetGrain<IGrainCallFilterTestGrain>(0);

            var result = await grain.Execute(false, false, false);
            Assert.Equal("I will not misbehave!", result);

            await Assert.ThrowsAsync<InvalidOperationException>(() => grain.Execute(true, false, false));
            await Assert.ThrowsAsync<InvalidOperationException>(() => grain.Execute(false, false, true));
            await Assert.ThrowsAsync<InvalidOperationException>(() => grain.Execute(false, true, false));
        }

        /// <summary>
        /// Tests filters on just the grain level.
        /// </summary>
        /// <returns></returns>
        [Fact]
        public async Task GrainCallFilter_GrainLevel_Test()
        {
            var grain = this.GrainFactory.GetGrain<IMethodInterceptionGrain>(0);
            var result = await grain.One();
            Assert.Equal("intercepted one with no args", result);

            result = await grain.Echo("stao erom tae");
            Assert.Equal("eat more oats", result);// Grain interceptors should receive the MethodInfo of the implementation, not the interface.

            result = await grain.NotIntercepted();
            Assert.Equal("not intercepted", result);

            result = await grain.SayHello();
            Assert.Equal("Hello", result);
        }

        /// <summary>
        /// Tests filters on generic grains.
        /// </summary>
        /// <returns></returns>
        [Fact]
        public async Task GrainCallFilter_GenericGrain_Test()
        {
            var grain = this.GrainFactory.GetGrain<IGenericMethodInterceptionGrain<int>>(0);
            var result = await grain.GetInputAsString(679);
            Assert.Contains("Hah!", result);
            Assert.Contains("679", result);

            result = await grain.SayHello();
            Assert.Equal("Hello", result);
        }

        [Fact]
        public async Task GrainCallFilter_ConstructedGenericInheritance_Test()
        {
            var grain = this.GrainFactory.GetGrain<ITrickyMethodInterceptionGrain>(0);

            var result = await grain.GetInputAsString("2014-12-19T14:32:50Z");
            Assert.Contains("Hah!", result);
            Assert.Contains("2014-12-19T14:32:50Z", result);

            result = await grain.SayHello();
            Assert.Equal("Hello", result);

            var bestNumber = await grain.GetBestNumber();
            Assert.Equal(38, bestNumber);

            result = await grain.GetInputAsString(true);
            Assert.Contains(true.ToString(CultureInfo.InvariantCulture), result);
        }
    }

    public class SiloInvokerTestStartup
    {
        private const string Key = GrainCallFilterTestConstants.Key;

        public IServiceProvider ConfigureServices(IServiceCollection services)
        {
            services.AddGrainCallFilter(context =>
            {
                if (string.Equals(context.Method.Name, nameof(IGrainCallFilterTestGrain.GetRequestContext)))
                {
                    if (RequestContext.Get(Key) != null) throw new InvalidOperationException();
                    RequestContext.Set(Key, "g");
                }

                return context.Invoke();
            });

            services.AddGrainCallFilter(context =>
            {
                if (string.Equals(context.Method.Name, nameof(IGrainCallFilterTestGrain.GetRequestContext)))
                {
                    var value = RequestContext.Get(Key) as string;
                    if (value != null) RequestContext.Set(Key, value + 'r');
                }

                return context.Invoke();
            });

            return services.BuildServiceProvider();
        }
    }

    public class PreInvokeCallbackBootrstrapProvider : IBootstrapProvider
    {
        public string Name { get; private set; }

        public Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
#pragma warning disable 618
            providerRuntime.SetInvokeInterceptor((method, request, grain, invoker) =>
#pragma warning restore 618
            {
                if (string.Equals(method.Name, nameof(IGrainCallFilterTestGrain.GetRequestContext)))
                {
                    var value = RequestContext.Get(GrainCallFilterTestConstants.Key) as string;
                    if (value != null) RequestContext.Set(GrainCallFilterTestConstants.Key, value + 'a');
                }

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
