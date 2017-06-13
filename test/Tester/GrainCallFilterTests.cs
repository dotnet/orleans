using System;
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
using UnitTests.Grains;
using Xunit;

namespace UnitTests.General
{
    [TestCategory("BVT"), TestCategory("GrainCallFilter")]
    public class GrainCallFilterTests : OrleansTestingBase, IClassFixture<GrainCallFilterTests.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
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
        }

        private readonly Fixture fixture;

        public GrainCallFilterTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        /// <summary>
        /// Ensures that grain call filters are invoked around method calls in the correct order.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        [Fact]
        public async Task GrainCallFilter_Order_Test()
        {
            var grain = this.fixture.GrainFactory.GetGrain<IGrainCallFilterTestGrain>(random.Next());

            // This grain method reads the context and returns it
            var context = await grain.GetRequestContext();
            Assert.NotNull(context);
            Assert.Equal("123456", context);
        }
        
        /// <summary>
        /// Ensures that the invocation interceptor is invoked for stream subscribers.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        [Fact]
        public async Task GrainCallFilter_Stream_Test()
        {
            var streamProvider = this.fixture.Client.GetStreamProvider("SMSProvider");
            var id = Guid.NewGuid();
            var stream = streamProvider.GetStream<int>(id, "InterceptedStream");
            var grain = this.fixture.GrainFactory.GetGrain<IStreamInterceptionGrain>(id);

            // The intercepted grain should double the value passed to the stream.
            const int testValue = 43;
            await stream.OnNextAsync(testValue);
            var actual = await grain.GetLastStreamValue();
            Assert.Equal(testValue * 2, actual);
        }

        /// <summary>
        /// Tests that some invalid usages of invoker interceptors are denied.
        /// </summary>
        /// <returns></returns>
        [Fact]
        public async Task GrainCallFilter_InvalidOrder_Test()
        {
            var grain = this.fixture.GrainFactory.GetGrain<IGrainCallFilterTestGrain>(0);

            var result = await grain.CallWithBadInterceptors(false, false, false);
            Assert.Equal("I will not misbehave!", result);

            await Assert.ThrowsAsync<InvalidOperationException>(() => grain.CallWithBadInterceptors(true, false, false));
            await Assert.ThrowsAsync<InvalidOperationException>(() => grain.CallWithBadInterceptors(false, false, true));
            await Assert.ThrowsAsync<InvalidOperationException>(() => grain.CallWithBadInterceptors(false, true, false));
        }

        /// <summary>
        /// Tests filters on just the grain level.
        /// </summary>
        /// <returns></returns>
        [Fact]
        public async Task GrainCallFilter_GrainLevel_Test()
        {
            var grain = this.fixture.GrainFactory.GetGrain<IMethodInterceptionGrain>(0);
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
            var grain = this.fixture.GrainFactory.GetGrain<IGenericMethodInterceptionGrain<int>>(0);
            var result = await grain.GetInputAsString(679);
            Assert.Contains("Hah!", result);
            Assert.Contains("679", result);

            result = await grain.SayHello();
            Assert.Equal("Hello", result);
        }
        
        /// <summary>
        /// Tests filters on grains which implement multiple of the same generic interface.
        /// </summary>
        /// <returns></returns>
        [Fact]
        public async Task GrainCallFilter_ConstructedGenericInheritance_Test()
        {
            var grain = this.fixture.GrainFactory.GetGrain<ITrickyMethodInterceptionGrain>(0);

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

        /// <summary>
        /// Tests that grain call filters can handle exceptions.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        [Fact]
        public async Task GrainCallFilter_ExceptionHandling_Test()
        {
            var grain = this.fixture.GrainFactory.GetGrain<IMethodInterceptionGrain>(random.Next());

            // This grain method throws, but the exception should be handled by one of the filters and converted
            // into a specific message.
            var result = await grain.Throw();
            Assert.NotNull(result);
            Assert.Equal("EXCEPTION! Oi!", result);
        }

        /// <summary>
        /// Tests that grain call filters can throw exceptions.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        [Fact]
        public async Task GrainCallFilter_FilterThrows_Test()
        {
            var grain = this.fixture.GrainFactory.GetGrain<IMethodInterceptionGrain>(random.Next());
            
            var exception = await Assert.ThrowsAsync<MethodInterceptionGrain.MyDomainSpecificException>(() => grain.FilterThrows());
            Assert.NotNull(exception);
            Assert.Equal("Filter THROW!", exception.Message);
        }

        /// <summary>
        /// Tests that if a grain call filter sets an incorrect result type for <see cref="Orleans.IGrainCallContext.Result"/>,
        /// an exception is thrown on the caller.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        [Fact]
        public async Task GrainCallFilter_SetIncorrectResultType_Test()
        {
            var grain = this.fixture.GrainFactory.GetGrain<IMethodInterceptionGrain>(random.Next());

            // This grain method throws, but the exception should be handled by one of the filters and converted
            // into a specific message.
            await Assert.ThrowsAsync<InvalidCastException>(() => grain.IncorrectResultType());
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
                    RequestContext.Set(Key, "1");
                }

                return context.Invoke();
            });

            services.AddGrainCallFilter(context =>
            {
                if (string.Equals(context.Method.Name, nameof(IGrainCallFilterTestGrain.GetRequestContext)))
                {
                    var value = RequestContext.Get(Key) as string;
                    if (value != null) RequestContext.Set(Key, value + '2');
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
                    if (value != null) RequestContext.Set(GrainCallFilterTestConstants.Key, value + '3');
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
