using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;
using Orleans.Hosting;
using Orleans.Serialization;

namespace UnitTests.General
{
    [TestCategory("BVT"), TestCategory("GrainCallFilter")]
    public class GrainCallFilterTests : OrleansTestingBase, IClassFixture<GrainCallFilterTests.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.ConfigureHostConfiguration(TestDefaultConfiguration.ConfigureHostConfiguration);
                builder.AddSiloBuilderConfigurator<SiloInvokerTestSiloBuilderConfigurator>();
                builder.ConfigureLegacyConfiguration(legacy =>
                {
                    legacy.ClusterConfiguration.AddMemoryStorageProvider("Default");
                    legacy.ClusterConfiguration.AddMemoryStorageProvider("PubSubStore");
                    legacy.ClusterConfiguration.AddSimpleMessageStreamProvider("SMSProvider");
                    legacy.ClientConfiguration.AddSimpleMessageStreamProvider("SMSProvider");
                });
            }

            private class SiloInvokerTestSiloBuilderConfigurator : ISiloBuilderConfigurator
            {
                public void Configure(ISiloHostBuilder hostBuilder)
                {
                    hostBuilder.ConfigureServices((hostBuilderContext, services) =>
                    {
                        services.AddGrainCallFilter(context =>
                        {
                            if (string.Equals(context.Method.Name, nameof(IGrainCallFilterTestGrain.GetRequestContext)))
                            {
                                if (RequestContext.Get(GrainCallFilterTestConstants.Key) != null) throw new InvalidOperationException();
                                RequestContext.Set(GrainCallFilterTestConstants.Key, "1");
                            }

                            return context.Invoke();
                        });

                        services.AddGrainCallFilter<GrainCallFilterWithDependencies>();
                    });
                }

            }
        }

        [SuppressMessage("ReSharper", "NotAccessedField.Local")]
        public class GrainCallFilterWithDependencies : IGrainCallFilter
        {
            private readonly SerializationManager serializationManager;
            private readonly Silo silo;
            private readonly IGrainFactory grainFactory;

            public GrainCallFilterWithDependencies(SerializationManager serializationManager, Silo silo, IGrainFactory grainFactory)
            {
                this.serializationManager = serializationManager;
                this.silo = silo;
                this.grainFactory = grainFactory;
            }

            public Task Invoke(IGrainCallContext context)
            {
                if (string.Equals(context.Method.Name, nameof(IGrainCallFilterTestGrain.GetRequestContext)))
                {
                    if (RequestContext.Get(GrainCallFilterTestConstants.Key) is string value)
                    {
                        RequestContext.Set(GrainCallFilterTestConstants.Key, value + '2');
                    }
                }

                return context.Invoke();
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
        [Fact]
        public async Task GrainCallFilter_Order_Test()
        {
            var grain = this.fixture.GrainFactory.GetGrain<IGrainCallFilterTestGrain>(random.Next());

            // This grain method reads the context and returns it
            var context = await grain.GetRequestContext();
            Assert.NotNull(context);
            Assert.Equal("1234", context);
        }
        
        /// <summary>
        /// Ensures that the invocation interceptor is invoked for stream subscribers.
        /// </summary>
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
        [Fact]
        public async Task GrainCallFilter_SetIncorrectResultType_Test()
        {
            var grain = this.fixture.GrainFactory.GetGrain<IMethodInterceptionGrain>(random.Next());

            // This grain method throws, but the exception should be handled by one of the filters and converted
            // into a specific message.
            await Assert.ThrowsAsync<InvalidCastException>(() => grain.IncorrectResultType());
        }
    }
}
