using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.General
{
    public class DiagnosticTests : OrleansTestingBase, IClassFixture<DiagnosticTests.Fixture>
    {
        private static readonly ActivityListener activityListener;

        static DiagnosticTests()
        {
            activityListener = new()
            {
                ShouldListenTo = p => p.Name == ActivityPropagationGrainCallFilter.ActivitySourceName,
                Sample = Sample,
                SampleUsingParentId = SampleUsingParentId
            };
            static ActivitySamplingResult Sample(ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.PropagationData;
            static ActivitySamplingResult SampleUsingParentId(ref ActivityCreationOptions<string> options) => ActivitySamplingResult.PropagationData;
        }
        public class Fixture : BaseTestClusterFixture
        {
            private readonly ActivityIdFormat format = Activity.DefaultIdFormat;

            public Fixture()
            {
                Activity.DefaultIdFormat = ActivityIdFormat.W3C;
            }

            public override Task DisposeAsync()
            {
                Activity.DefaultIdFormat = format;
                return base.DisposeAsync();
            }

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
                        .AddSimpleMessageStreamProvider("SMSProvider")
                        .AddMemoryGrainStorageAsDefault()
                        .AddMemoryGrainStorage("PubSubStore");
            }

            private class ClientConfigurator : IClientBuilderConfigurator
            {
                public void Configure(IConfiguration configuration, IClientBuilder clientBuilder) =>
                    clientBuilder
                        .AddActivityPropagation()
                        .AddSimpleMessageStreamProvider("SMSProvider");
            }
        }

        private readonly Fixture fixture;

        public DiagnosticTests(Fixture fixture)
        {
            this.fixture = fixture;
            ActivitySource.AddActivityListener(activityListener);
        }

        [Fact]
        public async Task WithoutParentActivity()
        {
            await Test(fixture.GrainFactory);
            await Test(fixture.Client);

            static async Task Test(IGrainFactory grainFactory)
            {
                var grain = grainFactory.GetGrain<IActivityGrain>(random.Next());

                var result = await grain.GetActivityId();

                Assert.NotNull(result);
                Assert.NotEmpty(result.Id);
                Assert.Null(result.TraceState);
            }
        }

        [Fact]
        public async Task WithParentActivity()
        {
            var activity = new Activity("SomeName");
            activity.TraceStateString = "traceState";
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
                var grain = grainFactory.GetGrain<IActivityGrain>(random.Next());

                var result = await grain.GetActivityId();

                Assert.NotNull(result);
                Assert.NotNull(result.Id);
                Assert.Contains(activity.TraceId.ToHexString(), result.Id); // ensure, that trace id is persisted.
                Assert.Equal(result.TraceState, activity.TraceStateString);
            }
        }
    }
}
