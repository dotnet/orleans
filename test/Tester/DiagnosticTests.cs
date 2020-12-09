using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.General;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.General
{
    public class DiagnosticTests : OrleansTestingBase, IClassFixture<DiagnosticTests.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.ConfigureHostConfiguration(TestDefaultConfiguration.ConfigureHostConfiguration);
                builder.AddSiloBuilderConfigurator<SiloInvokerTestSiloBuilderConfigurator>();
                builder.AddClientBuilderConfigurator<ClientConfigurator>();
                Activity.DefaultIdFormat = ActivityIdFormat.W3C;
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

            private ActivityIdFormat format = Activity.DefaultIdFormat;

            public override Task InitializeAsync()
            {
                Activity.DefaultIdFormat = ActivityIdFormat.W3C;
                return base.InitializeAsync();
            }

            public override Task DisposeAsync()
            {
                Activity.DefaultIdFormat = format;
                return base.DisposeAsync();
            }
        }

        private readonly Fixture fixture;

        public DiagnosticTests(Fixture fixture) => this.fixture = fixture;

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
            var baggage = new KeyValuePair<string, string>("key", "value");
            var activity = new Activity("SomeName");
            activity.TraceStateString = "traceState";
            activity.AddBaggage(baggage.Key, baggage.Value);
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
                Assert.Contains(baggage, result.Baggage);
            }
        }

        [Fact]
        public async Task WithDiagnosticListener()
        {
            await Test(fixture.GrainFactory);
            await Test(fixture.Client);

            async Task Test(IGrainFactory grainFactory)
            {
                var grain = grainFactory.GetGrain<IActivityGrain>(random.Next());

                var eventNames = new List<string>();
                var observer = new DiagnosticListenerObserver((k, v) => eventNames.Add(k));
                using (DiagnosticListener.AllListeners.Subscribe(observer))
                {
                    var result = await grain.GetActivityId();
                    Assert.NotNull(result);
                    Assert.NotNull(result.Id);
                }

                Assert.Contains(ActivityPropagationGrainCallFilter.ActivityStartNameIn, eventNames);
                Assert.Contains(ActivityPropagationGrainCallFilter.ActivityStartNameOut, eventNames);
            }
        }

        private class DiagnosticListenerObserver : IObserver<DiagnosticListener>,
            IObserver<KeyValuePair<string, object>>
        {
            private readonly Action<string, object> next;

            public DiagnosticListenerObserver(Action<string, object> next)
            {
                this.next = next;
            }

            public void OnCompleted() { }

            public void OnError(Exception error) { }

            public void OnNext(DiagnosticListener value)
            {
                if (value.Name == "Orleans")
                {
                    value.Subscribe(this);
                }
            }

            public void OnNext(KeyValuePair<string, object> value) => next(value.Key, value.Value);
        }
    }
}
