using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;
using Orleans.Hosting;
using Orleans.Providers;
using System.Threading;
using Orleans.Internal;

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
                builder.AddClientBuilderConfigurator<ClientConfigurator>();
            }

            private class SiloInvokerTestSiloBuilderConfigurator : ISiloConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)
                {
                    hostBuilder
                        .AddIncomingGrainCallFilter(context =>
                        {
                            if (string.Equals(context.InterfaceMethod?.Name, nameof(IGrainCallFilterTestGrain.GetRequestContext)))
                            {
                                if (RequestContext.Get(GrainCallFilterTestConstants.Key) != null) throw new InvalidOperationException();
                                RequestContext.Set(GrainCallFilterTestConstants.Key, "1");
                            }

                            if (string.Equals(context.InterfaceMethod?.Name, nameof(IGrainCallFilterTestGrain.SystemWideCallFilterMarker)))
                            {
                                // explicitely do not continue calling Invoke
                                return Task.CompletedTask;
                            }

                            return context.Invoke();
                        })
                        .AddIncomingGrainCallFilter<GrainCallFilterWithDependencies>()
                        .AddOutgoingGrainCallFilter(async ctx =>
                        {
                            if (ctx.InterfaceMethod?.Name == "Echo")
                            {
                                // Concatenate the input to itself.
                                var orig = (string)ctx.Request.GetArgument(0);
                                ctx.Request.SetArgument(0, orig + orig);
                            }

                            if (string.Equals(ctx.InterfaceMethod?.Name, nameof(IMethodInterceptionGrain.SystemWideCallFilterMarker)))
                            {
                                // explicitely do not continue calling Invoke
                                return;
                            }

                            await ctx.Invoke();
                        })
                        .AddMemoryStreams<DefaultMemoryMessageBodySerializer>("SMSProvider")
                        .AddMemoryGrainStorageAsDefault()
                        .AddMemoryGrainStorage("PubSubStore");
                }
            }

            private class ClientConfigurator : IClientBuilderConfigurator
            {
                public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
                {
                    clientBuilder
                        .AddOutgoingGrainCallFilter(RetryCertainCalls)
                        .AddOutgoingGrainCallFilter(async ctx =>
                        {
                            if (ctx.InterfaceMethod?.DeclaringType == typeof(IOutgoingMethodInterceptionGrain)
                                && ctx.InterfaceMethod?.Name == nameof(IOutgoingMethodInterceptionGrain.EchoViaOtherGrain))
                            {
                                ctx.Request.SetArgument(1, ((string)ctx.Request.GetArgument(1)).ToUpperInvariant());
                            }

                            await ctx.Invoke();

                            if (ctx.InterfaceMethod?.DeclaringType == typeof(IOutgoingMethodInterceptionGrain)
                                && ctx.InterfaceMethod?.Name == nameof(IOutgoingMethodInterceptionGrain.EchoViaOtherGrain))
                            {
                                var result = (Dictionary<string, object>)ctx.Result;
                                result["orig"] = result["result"];
                                result["result"] = "intercepted!";
                            }
                        })
                        .AddMemoryStreams<DefaultMemoryMessageBodySerializer>("SMSProvider");

                    static async Task RetryCertainCalls(IOutgoingGrainCallContext ctx)
                    {
                        var attemptsRemaining = 2;

                        while (attemptsRemaining > 0)
                        {
                            try
                            {
                                await ctx.Invoke();
                                return;
                            }
                            catch (ArgumentOutOfRangeException) when (attemptsRemaining > 1 && ctx.Grain is IOutgoingMethodInterceptionGrain)
                            {
                                if (string.Equals(ctx.InterfaceMethod?.Name, nameof(IOutgoingMethodInterceptionGrain.ThrowIfGreaterThanZero)) && ctx.Request.GetArgument(0) is int value)
                                {
                                    ctx.Request.SetArgument(0, value - 1);
                                }

                                --attemptsRemaining;
                            }
                        }
                    }
                }
            }
        }

        [SuppressMessage("ReSharper", "NotAccessedField.Local")]
        public class GrainCallFilterWithDependencies : IIncomingGrainCallFilter
        {
            private readonly Silo silo;
            private readonly IGrainFactory grainFactory;

            public GrainCallFilterWithDependencies(Silo silo, IGrainFactory grainFactory)
            {
                this.silo = silo;
                this.grainFactory = grainFactory;
            }

            public Task Invoke(IIncomingGrainCallContext context)
            {
                if (string.Equals(context.ImplementationMethod?.Name, nameof(IGrainCallFilterTestGrain.GetRequestContext)))
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
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        [Fact]
        public async Task GrainCallFilter_Outgoing_Test()
        {
            var grain = this.fixture.GrainFactory.GetGrain<IOutgoingMethodInterceptionGrain>(Random.Shared.Next());
            var grain2 = this.fixture.GrainFactory.GetGrain<IMethodInterceptionGrain>(Random.Shared.Next());

            // This grain method reads the context and returns it
            var result = await grain.EchoViaOtherGrain(grain2, "ab");

            // Original arg should have been:
            // 1. Converted to upper case on the way out of the client: ab -> AB.
            // 2. Doubled on the way out of grain1: AB -> ABAB.
            // 3. Reversed on the way in to grain2: ABAB -> BABA.
            Assert.Equal("BABA", result["orig"] as string);
            Assert.NotNull(result["result"]);
            Assert.Equal("intercepted!", result["result"]);
        }

        /// <summary>
        /// Ensures that grain call filters are invoked around method calls in the correct order.
        /// </summary>
        [Fact]
        public async Task GrainCallFilter_Incoming_Order_Test()
        {
            var grain = this.fixture.GrainFactory.GetGrain<IGrainCallFilterTestGrain>(Random.Shared.Next());

            // This grain method reads the context and returns it
            var context = await grain.GetRequestContext();
            Assert.NotNull(context);
            Assert.Equal("1234", context);
        }
        
        /// <summary>
        /// Ensures that the invocation interceptor is invoked for stream subscribers.
        /// </summary>
        [Fact]
        public async Task GrainCallFilter_Incoming_Stream_Test()
        {
            var streamProvider = this.fixture.Client.GetStreamProvider("SMSProvider");
            var id = Guid.NewGuid();
            var stream = streamProvider.GetStream<int>("InterceptedStream", id);
            var grain = this.fixture.GrainFactory.GetGrain<IStreamInterceptionGrain>(id);

            // The intercepted grain should double the value passed to the stream.
            const int testValue = 43;
            await stream.OnNextAsync(testValue);
            var cts = new CancellationTokenSource(1000);
            int actual = 0;
            while (!cts.IsCancellationRequested)
            {
                actual = await grain.GetLastStreamValue();
                if (actual != 0) break;
            }
            Assert.Equal(testValue * 2, actual);
        }

        /// <summary>
        /// Tests that an incoming call filter can retry calls.
        /// </summary>
        [Fact]
        public async Task GrainCallFilter_Incoming_Retry_Test()
        {
            var grain = this.fixture.GrainFactory.GetGrain<IGrainCallFilterTestGrain>(0);

            var result = await grain.ThrowIfGreaterThanZero(1);
            Assert.Equal("Thanks for nothing", result);

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => grain.ThrowIfGreaterThanZero(2));
        }

        /// <summary>
        /// Tests that an incoming call filter works with HashSet.
        /// </summary>
        [Fact]
        public async Task GrainCallFilter_Incoming_HashSet_Test()
        {
            var grain = this.fixture.GrainFactory.GetGrain<IGrainCallFilterTestGrain>(0);

            var result = await grain.SumSet(new HashSet<int> { 1, 2, 3 });
            Assert.Equal(6, result);
        }

        /// <summary>
        /// Tests that an ougoing call filter can retry calls.
        /// </summary>
        [Fact]
        public async Task GrainCallFilter_Outgoing_Retry_Test()
        {
            var grain = this.fixture.GrainFactory.GetGrain<IOutgoingMethodInterceptionGrain>(0);

            var result = await grain.ThrowIfGreaterThanZero(1);
            Assert.Equal("Thanks for nothing", result);

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => grain.ThrowIfGreaterThanZero(2));
        }

        /// <summary>
        /// Tests filters on just the grain level.
        /// </summary>
        [Fact]
        public async Task GrainCallFilter_Incoming_GrainLevel_Test()
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
        public async Task GrainCallFilter_Incoming_GenericGrain_Test()
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
        public async Task GrainCallFilter_Incoming_ConstructedGenericInheritance_Test()
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
        public async Task GrainCallFilter_Incoming_ExceptionHandling_Test()
        {
            var grain = this.fixture.GrainFactory.GetGrain<IMethodInterceptionGrain>(Random.Shared.Next());

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
        public async Task GrainCallFilter_Incoming_FilterThrows_Test()
        {
            var grain = this.fixture.GrainFactory.GetGrain<IMethodInterceptionGrain>(Random.Shared.Next());
            
            var exception = await Assert.ThrowsAsync<MethodInterceptionGrain.MyDomainSpecificException>(() => grain.FilterThrows());
            Assert.NotNull(exception);
            Assert.Equal("Filter THROW!", exception.Message);
        }

        /// <summary>
        /// Tests that if a grain call filter sets an incorrect result type for <see cref="Orleans.IGrainCallContext.Result"/>,
        /// an exception is thrown on the caller.
        /// </summary>
        [Fact]
        public async Task GrainCallFilter_Incoming_SetIncorrectResultType_Test()
        {
            var grain = this.fixture.GrainFactory.GetGrain<IMethodInterceptionGrain>(Random.Shared.Next());

            // This grain method throws, but the exception should be handled by one of the filters and converted
            // into a specific message.
            await Assert.ThrowsAsync<InvalidCastException>(() => grain.IncorrectResultType());
        }

        /// <summary>
        /// Tests that <see cref="IIncomingGrainCallContext.ImplementationMethod"/> and <see cref="IGrainCallContext.InterfaceMethod"/> are non-null
        /// for a call made to a grain and that they match the correct methods.
        /// </summary>
        [Fact]
        public async Task GrainCallFilter_Incoming_GenericInterface_ConcreteGrain_Test()
        {
            var id = Random.Shared.Next();
            var hungry = this.fixture.GrainFactory.GetGrain<IHungryGrain<Apple>>(id);
            var caterpillar = this.fixture.GrainFactory.GetGrain<ICaterpillarGrain>(id);
            var omnivore = this.fixture.GrainFactory.GetGrain<IOmnivoreGrain>(id);

            RequestContext.Set("tag", "hungry-eat");
            await hungry.Eat(new Apple());
            await ((IHungryGrain<Apple>)caterpillar).Eat(new Apple());

            RequestContext.Set("tag", "omnivore-eat");
            await omnivore.Eat("string");
            await ((IOmnivoreGrain)caterpillar).Eat("string");

            RequestContext.Set("tag", "caterpillar-eat");
            await caterpillar.Eat("string");

            RequestContext.Set("tag", "hungry-eatwith");
            await caterpillar.EatWith(new Apple(), "butter");
            await hungry.EatWith(new Apple(), "butter");
        }

        /// <summary>
        /// Tests that if a grain call filter does not call <see cref="IGrainCallContext.Invoke"/>,
        /// an exception is thrown on the caller.
        /// </summary>
        [Fact]
        public async Task GrainCallFilter_Incoming_SystemWideDoesNotCallContextInvoke_Test()
        {
            var grain = this.fixture.GrainFactory.GetGrain<IGrainCallFilterTestGrain>(Random.Shared.Next());

            // The call filter doesn't continue the Invoke chain, but the error state should be thrown as an
            // InvalidOperationException, not an NullReferenceException.
            await Assert.ThrowsAsync<InvalidOperationException>(() => grain.SystemWideCallFilterMarker());
        }

        /// <summary>
        /// Tests that if a grain call filter does not call <see cref="IGrainCallContext.Invoke"/>,
        /// an exception is thrown on the caller.
        /// </summary>
        [Fact]
        public async Task GrainCallFilter_Incoming_GrainSpecificDoesNotCallContextInvoke_Test()
        {
            var grain = this.fixture.GrainFactory.GetGrain<IGrainCallFilterTestGrain>(Random.Shared.Next());

            // The call filter doesn't continue the Invoke chain, but the error state should be thrown as an
            // InvalidOperationException, not an NullReferenceException.
            await Assert.ThrowsAsync<InvalidOperationException>(() => grain.GrainSpecificCallFilterMarker());
        }

        /// <summary>
        /// Tests that if an outgoing grain call filter does not call <see cref="IGrainCallContext.Invoke"/>,
        /// an exception is thrown on the caller.
        /// </summary>
        [Fact]
        public async Task GrainCallFilter_Outgoing_SystemWideDoesNotCallContextInvoke_Test()
        {
            var grain = this.fixture.GrainFactory.GetGrain<IMethodInterceptionGrain>(Random.Shared.Next());

            // The call filter doesn't continue the Invoke chain, but the error state should be thrown as an
            // InvalidOperationException, not an NullReferenceException.
            await Assert.ThrowsAsync<InvalidOperationException>(() => grain.SystemWideCallFilterMarker());
        }
    }
}
