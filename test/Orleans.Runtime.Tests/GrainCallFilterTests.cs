using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Orleans.Streams;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;
using Orleans.Providers;
using System.Diagnostics;

namespace UnitTests.General
{
    internal interface IMyRegularInterface
    {
        [Alias("Set")]
        ValueTask SetExtensionValue(int value);

        [Alias("Get")]
        ValueTask<int> GetExtensionValue();
    }

    internal interface IMyOtherInterface
    {
        [Alias("Set")]
        ValueTask SetExtensionValue(int value);

        [Alias("Get")]
        ValueTask<int> GetExtensionValue();
    }

    internal interface IMyGrainExtension : IGrainExtension, IMyRegularInterface, IMyOtherInterface
    {
    }

    internal sealed class MyGrainExtension : IMyGrainExtension
    {
        private int _value;

        ValueTask<int> IMyRegularInterface.GetExtensionValue() => new(_value);

        public ValueTask SetExtensionValue(int value)
        {
            _value = value;
            return default;
        }

        ValueTask<int> IMyOtherInterface.GetExtensionValue() => new(100 + _value);
    }

    /// <summary>
    /// Comprehensive tests for Orleans grain call filters (interceptors).
    /// 
    /// Grain call filters provide AOP-style interception of grain method calls, enabling:
    /// - Cross-cutting concerns (logging, monitoring, security)
    /// - Request/response manipulation
    /// - Retry logic and error handling
    /// - Method call metrics and tracing
    /// 
    /// Orleans supports both incoming filters (executed on the target grain/silo) and
    /// outgoing filters (executed on the calling grain/client). Filters can be registered:
    /// - System-wide (all grains)
    /// - Per-grain-type
    /// 
    /// These tests verify filter execution order, context propagation, exception handling,
    /// and integration with various Orleans features like streaming and observers.
    /// </summary>
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
                        .AddGrainExtension<IMyGrainExtension, MyGrainExtension>()
                        // System-wide incoming filter - executes for ALL grain calls on this silo
                        .AddIncomingGrainCallFilter(context =>
                        {
                            Assert.NotNull(context);
                            Assert.NotNull(context.InterfaceMethod);
                            Assert.NotNull(context.Grain);
                            Assert.NotNull(context.ImplementationMethod);
                            Assert.NotNull(context.TargetContext);
                            Assert.NotEmpty(context.InterfaceName);
                            Assert.NotEmpty(context.MethodName);
                            Assert.False(context.TargetId.IsDefault);
                            Assert.False(context.InterfaceType.IsDefault);

                            // Test 1: Verify RequestContext propagation through filters
                            // Each filter adds a digit to build up "1234"
                            if (string.Equals(context.InterfaceMethod.Name, nameof(IGrainCallFilterTestGrain.GetRequestContext)))
                            {
                                if (RequestContext.Get(GrainCallFilterTestConstants.Key) != null) throw new InvalidOperationException();
                                RequestContext.Set(GrainCallFilterTestConstants.Key, "1");
                            }

                            // Test 2: Verify behavior when filter doesn't call context.Invoke()
                            // This should result in an InvalidOperationException for the caller
                            if (string.Equals(context.InterfaceMethod.Name, nameof(IGrainCallFilterTestGrain.SystemWideCallFilterMarker)))
                            {
                                // explicitly do not continue calling Invoke
                                return Task.CompletedTask;
                            }

                            // Test 3: Demonstrate request manipulation - negate the value
                            // This shows filters can modify method arguments before execution
                            if (string.Equals(context.InterfaceMethod.Name, nameof(IMyGrainExtension.SetExtensionValue)))
                            {
                                context.Request.SetArgument(0, (int)context.Request.GetArgument(0) * -1);
                            }

                            return context.Invoke();
                        })
                        .AddIncomingGrainCallFilter<GrainCallFilterWithDependencies>()
                        // System-wide outgoing filter - executes when this silo calls other grains
                        .AddOutgoingGrainCallFilter(async ctx =>
                        {
                            // Modify outgoing Echo calls by doubling the string argument
                            if (ctx.InterfaceMethod?.Name == "Echo")
                            {
                                // Concatenate the input to itself.
                                var orig = (string)ctx.Request.GetArgument(0);
                                ctx.Request.SetArgument(0, orig + orig);
                            }

                            if (string.Equals(ctx.InterfaceMethod?.Name, nameof(IMethodInterceptionGrain.SystemWideCallFilterMarker)))
                            {
                                // explicitly do not continue calling Invoke
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
                        .AddIncomingGrainCallFilter(context =>
                        {
                            Assert.NotNull(context);
                            Assert.NotNull(context.InterfaceMethod);
                            Assert.NotNull(context.Grain);
                            Assert.NotNull(context.ImplementationMethod);
                            Assert.NotNull(context.TargetContext);
                            Assert.NotEmpty(context.InterfaceName);
                            Assert.NotEmpty(context.MethodName);
                            Assert.False(context.TargetId.IsDefault);
                            Assert.False(context.InterfaceType.IsDefault);

                            if (string.Equals(context.InterfaceMethod.Name, nameof(IGrainCallFilterTestGrainObserver.GetRequestContext)))
                            {
                                if (RequestContext.Get(GrainCallFilterTestConstants.Key) != null) throw new InvalidOperationException();
                                RequestContext.Set(GrainCallFilterTestConstants.Key, "1");
                            }

                            if (string.Equals(context.InterfaceMethod.Name, nameof(IGrainCallFilterTestGrainObserver.SystemWideCallFilterMarker)))
                            {
                                // explicitly do not continue calling Invoke
                                return Task.CompletedTask;
                            }

                            return context.Invoke();
                        })
                        .AddIncomingGrainCallFilter<GrainCallFilterWithDependencies>()
                        .AddOutgoingGrainCallFilter(RetryCertainCalls)
                        .AddOutgoingGrainCallFilter(async context =>
                        {
                            Assert.NotNull(context);
                            Assert.NotNull(context.InterfaceMethod);
                            Assert.NotNull(context.Grain);
                            Assert.NotEmpty(context.InterfaceName);
                            Assert.NotEmpty(context.MethodName);
                            Assert.False(context.TargetId.IsDefault);
                            Assert.False(context.InterfaceType.IsDefault);
                            if (context.InterfaceMethod?.DeclaringType == typeof(IOutgoingMethodInterceptionGrain)
                                && context.InterfaceMethod?.Name == nameof(IOutgoingMethodInterceptionGrain.EchoViaOtherGrain))
                            {
                                context.Request.SetArgument(1, ((string)context.Request.GetArgument(1)).ToUpperInvariant());
                            }

                            await context.Invoke();

                            if (context.InterfaceMethod?.DeclaringType == typeof(IOutgoingMethodInterceptionGrain)
                                && context.InterfaceMethod?.Name == nameof(IOutgoingMethodInterceptionGrain.EchoViaOtherGrain))
                            {
                                var result = (Dictionary<string, object>)context.Result;
                                result["orig"] = result["result"];
                                result["result"] = "intercepted!";
                            }
                        })
                        .AddMemoryStreams<DefaultMemoryMessageBodySerializer>("SMSProvider");

                    // Demonstrates retry logic in outgoing filters
                    // This filter retries failed calls by modifying the argument
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
                                // Retry by decrementing the problematic value
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

        /// <summary>
        /// Demonstrates that grain call filters can use dependency injection.
        /// This filter receives IGrainFactory through constructor injection,
        /// showing that filters are full participants in the DI container.
        /// </summary>
        [SuppressMessage("ReSharper", "NotAccessedField.Local")]
        public class GrainCallFilterWithDependencies(IGrainFactory grainFactory) : IIncomingGrainCallFilter
        {
            public Task Invoke(IIncomingGrainCallContext context)
            {
                Assert.NotNull(grainFactory);
                // Continue building the RequestContext value: "1" -> "12"
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
        /// Tests the complete outgoing filter pipeline with request/response manipulation.
        /// Verifies that:
        /// 1. Client outgoing filter converts argument to uppercase: ab -> AB
        /// 2. Grain1 outgoing filter doubles the string: AB -> ABAB
        /// 3. Grain2 incoming filter reverses it: ABAB -> BABA
        /// 4. Response is intercepted and modified on the way back
        /// </summary>
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
        /// Verifies the execution order of multiple incoming grain call filters.
        /// The RequestContext should accumulate values in order: "1" -> "12" -> "123" -> "1234"
        /// where each digit is added by a different filter in the pipeline.
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
        /// Tests that grain call filters are properly invoked for streaming scenarios.
        /// When a grain receives stream events, the incoming filters should still execute,
        /// allowing for stream event manipulation or monitoring.
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
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            int actual = 0;
            while (!cts.IsCancellationRequested)
            {
                actual = await grain.GetLastStreamValue();
                if (actual != 0) break;
            }
            
            Assert.Equal(testValue * 2, actual);
        }

        /// <summary>
        /// Demonstrates retry logic in incoming filters.
        /// The filter modifies the failing argument value to make the call succeed.
        /// Shows how filters can implement resilience patterns.
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
        /// Tests that an outgoing call filter can retry calls.
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
        /// Tests grain-specific filters.
        /// These filters only execute for specific grain types, not system-wide.
        /// Demonstrates:
        /// - Method interception with custom logic
        /// - Selective filtering (some methods not intercepted)
        /// - Access to implementation method info
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
        /// Verifies that grain call filters work correctly with generic grain types.
        /// Generic grains pose special challenges for reflection-based systems,
        /// so this ensures filters can properly intercept calls to generic grain methods.
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
        /// Demonstrates exception handling in grain call filters.
        /// Filters can catch exceptions from grain methods and:
        /// - Transform them into different responses
        /// - Log and rethrow
        /// - Convert to domain-specific exceptions
        /// This test shows converting an exception into a success response.
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
        /// Tests that grain call filters properly intercept calls to grain extensions.
        /// Grain extensions are a way to add additional interfaces to existing grains.
        /// This verifies that filters work correctly even when methods are called
        /// through extension interfaces rather than the primary grain interface.
        /// </summary>
        [Fact]
        public async Task GrainCallFilter_GrainExtension()
        {
            var grain = this.fixture.GrainFactory.GetGrain<IMethodInterceptionGrain>(Random.Shared.Next());
            var extension = grain.AsReference<IMyGrainExtension>();

            await ((IMyRegularInterface)extension).SetExtensionValue(42);
            var result = await ((IMyRegularInterface)extension).GetExtensionValue();
            Assert.Equal(-42, result);

            result = await ((IMyOtherInterface)extension).GetExtensionValue();
            Assert.Equal(100-42, result);
        }

        /// <summary>
        /// Verifies correct method resolution when grains implement generic interfaces.
        /// Tests complex inheritance scenarios where:
        /// - A grain implements multiple generic interfaces
        /// - Methods have the same name across interfaces
        /// - The filter must correctly identify which interface method was called
        /// This is critical for filters that need to apply interface-specific logic.
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

        /// <summary>
        /// Ensures that grain call filters are invoked around method calls in the correct order.
        /// </summary>
        [Fact]
        public async Task Observer_GrainCallFilter_Incoming_Order_Test()
        {
            var observer = new GrainCallFilterTestGrainObserver();
            var grain = this.fixture.GrainFactory.CreateObjectReference<IGrainCallFilterTestGrainObserver>(observer);

            // This grain method reads the context and returns it
            var context = await grain.GetRequestContext();
            Assert.NotNull(context);
            Assert.Equal("1234", context);
        }

        /// <summary>
        /// Tests that an incoming call filter can retry calls to an observer.
        /// </summary>
        [Fact]
        public async Task Observer_GrainCallFilter_Incoming_Retry_Test()
        {
            var observer = new GrainCallFilterTestGrainObserver();
            var grain = this.fixture.GrainFactory.CreateObjectReference<IGrainCallFilterTestGrainObserver>(observer);

            var result = await grain.ThrowIfGreaterThanZero(1);
            Assert.Equal("Thanks for nothing", result);

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => grain.ThrowIfGreaterThanZero(2));
        }

        /// <summary>
        /// Tests that an incoming call filter works on an observer with HashSet.
        /// </summary>
        [Fact]
        public async Task Observer_GrainCallFilter_Incoming_HashSet_Test()
        {
            var observer = new GrainCallFilterTestGrainObserver();
            var grain = this.fixture.GrainFactory.CreateObjectReference<IGrainCallFilterTestGrainObserver>(observer);

            var result = await grain.SumSet(new HashSet<int> { 1, 2, 3 });
            Assert.Equal(6, result);
        }

        /// <summary>
        /// Tests that if a grain call filter does not call <see cref="IGrainCallContext.Invoke"/>,
        /// an exception is thrown on the caller.
        /// </summary>
        [Fact]
        public async Task Observer_GrainCallFilter_Incoming_SystemWideDoesNotCallContextInvoke_Test()
        {
            var observer = new GrainCallFilterTestGrainObserver();
            var grain = this.fixture.GrainFactory.CreateObjectReference<IGrainCallFilterTestGrainObserver>(observer);

            // The call filter doesn't continue the Invoke chain, but the error state should be thrown as an
            // InvalidOperationException, not an NullReferenceException.
            await Assert.ThrowsAsync<InvalidOperationException>(() => grain.SystemWideCallFilterMarker());
        }

        /// <summary>
        /// Tests that if a grain call filter does not call <see cref="IGrainCallContext.Invoke"/>,
        /// an exception is thrown on the caller.
        /// </summary>
        [Fact]
        public async Task Observer_GrainCallFilter_Incoming_GrainSpecificDoesNotCallContextInvoke_Test()
        {
            var observer = new GrainCallFilterTestGrainObserver();
            var grain = this.fixture.GrainFactory.CreateObjectReference<IGrainCallFilterTestGrainObserver>(observer);

            // The call filter doesn't continue the Invoke chain, but the error state should be thrown as an
            // InvalidOperationException, not an NullReferenceException.
            await Assert.ThrowsAsync<InvalidOperationException>(() => grain.GrainSpecificCallFilterMarker());
        }

        /// <summary>
        /// Tests filters on just the grain level.
        /// </summary>
        [Fact]
        public async Task Observer_GrainCallFilter_Incoming_GrainLevel_Test()
        {
            var observer = new MethodInterceptionGrainObserver();
            var grain = this.fixture.GrainFactory.CreateObjectReference<IMethodInterceptionGrainObserver>(observer);
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
        public async Task Observer_GrainCallFilter_Incoming_GenericGrain_Test()
        {
            var observer = new GenericMethodInterceptionGrainObserver<int>();
            var grain = this.fixture.GrainFactory.CreateObjectReference<IGenericMethodInterceptionGrainObserver<int>>(observer);
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
        public async Task Observer_GrainCallFilter_Incoming_ConstructedGenericInheritance_Test()
        {
            var observer = new TrickyInterceptionGrainObserver();
            var grain = this.fixture.GrainFactory.CreateObjectReference<ITrickyMethodInterceptionGrainObserver>(observer);

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
        public async Task Observer_GrainCallFilter_Incoming_ExceptionHandling_Test()
        {
            var observer = new MethodInterceptionGrainObserver();
            var grain = this.fixture.GrainFactory.CreateObjectReference<IMethodInterceptionGrainObserver>(observer);

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
        public async Task Observer_GrainCallFilter_Incoming_FilterThrows_Test()
        {
            var observer = new MethodInterceptionGrainObserver();
            var grain = this.fixture.GrainFactory.CreateObjectReference<IMethodInterceptionGrainObserver>(observer);
            
            var exception = await Assert.ThrowsAsync<MethodInterceptionGrainObserver.MyDomainSpecificException>(() => grain.FilterThrows());
            Assert.NotNull(exception);
            Assert.Equal("Filter THROW!", exception.Message);
        }

        /// <summary>
        /// Tests that if a grain call filter sets an incorrect result type for <see cref="Orleans.IGrainCallContext.Result"/>,
        /// an exception is thrown on the caller.
        /// </summary>
        [Fact]
        public async Task Observer_GrainCallFilter_Incoming_SetIncorrectResultType_Test()
        {
            var observer = new MethodInterceptionGrainObserver();
            var grain = this.fixture.GrainFactory.CreateObjectReference<IMethodInterceptionGrainObserver>(observer);

            // This grain method throws, but the exception should be handled by one of the filters and converted
            // into a specific message.
            await Assert.ThrowsAsync<InvalidCastException>(() => grain.IncorrectResultType());
        }
    }
}
