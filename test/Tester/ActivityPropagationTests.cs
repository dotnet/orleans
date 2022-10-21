using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Orleans;
using Orleans.Hosting;
using Orleans.Internal;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.General
{
    public class ActivityPropagationTests : OrleansTestingBase, IClassFixture<ActivityPropagationTests.Fixture>
    {
        private static readonly ActivityListener Listener;

        static ActivityPropagationTests()
        {
            Listener = new()
            {
                ShouldListenTo = p => p.Name == ActivityPropagationGrainCallFilter.ApplicationGrainActivitySourceName,
                Sample = Sample,
                SampleUsingParentId = SampleUsingParentId,
            };

            static ActivitySamplingResult Sample(ref ActivityCreationOptions<ActivityContext> options)
            {
                //Trace id has to be accessed in sample to reproduce the scenario when SetParentId does not work
                var _ = options.TraceId; 
                return ActivitySamplingResult.PropagationData;
            };

            static ActivitySamplingResult SampleUsingParentId(ref ActivityCreationOptions<string> options)
            {
                //Trace id has to be accessed in sample to reproduce the scenario when SetParentId does not work
                var _ = options.TraceId;
                return ActivitySamplingResult.PropagationData;
            };
        }

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
                public void Configure(ISiloBuilder hostBuilder) =>
                    hostBuilder
                        .AddActivityPropagation()
                        .AddMemoryGrainStorageAsDefault()
                        .AddMemoryGrainStorage("PubSubStore");
            }

            private class ClientConfigurator : IClientBuilderConfigurator
            {
                public void Configure(IConfiguration configuration, IClientBuilder clientBuilder) =>
                    clientBuilder
                        .AddActivityPropagation();
            }
        }

        private readonly ActivityIdFormat defaultIdFormat;
        private readonly Fixture fixture;

        public ActivityPropagationTests(Fixture fixture)
        {
            defaultIdFormat = Activity.DefaultIdFormat;
            this.fixture = fixture;
            ActivitySource.AddActivityListener(Listener);
        }

        [Theory]
        [InlineData(ActivityIdFormat.W3C)]
        [InlineData(ActivityIdFormat.Hierarchical)]
        [TestCategory("BVT")]
        public async Task WithoutParentActivity(ActivityIdFormat idFormat)
        {
            Activity.DefaultIdFormat = idFormat;

            await Test(fixture.GrainFactory);
            await Test(fixture.Client);

            static async Task Test(IGrainFactory grainFactory)
            {
                var grain = grainFactory.GetGrain<IActivityGrain>(Random.Shared.Next());

                var result = await grain.GetActivityId();

                Assert.NotNull(result);
                Assert.NotEmpty(result.Id);
                Assert.Null(result.TraceState);
            }
        }

        [Fact]
        [TestCategory("BVT")]
        public async Task WithParentActivity_W3C()
        {
            Activity.DefaultIdFormat = ActivityIdFormat.W3C;

            var activity = new Activity("SomeName");
            activity.TraceStateString = "traceState";
            activity.AddBaggage("foo", "bar");
            activity.Start();

            try
            {
                await Test(fixture.GrainFactory);
                await Test(fixture.Client);
            }
            finally
            {
                activity.Stop();
            }

            async Task Test(IGrainFactory grainFactory)
            {
                var grain = grainFactory.GetGrain<IActivityGrain>(Random.Shared.Next());

                var result = await grain.GetActivityId();

                Assert.NotNull(result);
                Assert.NotNull(result.Id);
                Assert.Contains(activity.TraceId.ToHexString(), result.Id); // ensure, that trace id is persisted.
                Assert.Equal(activity.TraceStateString, result.TraceState);
                Assert.Equal(activity.Baggage, result.Baggage);
            }
        }

        [Fact]
        [TestCategory("BVT")]
        public async Task WithParentActivity_Hierarchical()
        {
            Activity.DefaultIdFormat = ActivityIdFormat.Hierarchical;

            var activity = new Activity("SomeName");
            activity.AddBaggage("foo", "bar");
            activity.Start();

            try
            {
                await Test(fixture.GrainFactory);
                await Test(fixture.Client);
            }
            finally
            {
                activity.Stop();
            }

            async Task Test(IGrainFactory grainFactory)
            {
                var grain = grainFactory.GetGrain<IActivityGrain>(Random.Shared.Next());

                var result = await grain.GetActivityId();

                Assert.NotNull(result);
                Assert.NotNull(result.Id);
                Assert.StartsWith(activity.Id, result.Id);
                Assert.Equal(activity.Baggage, result.Baggage);
            }
        }
    }
}
