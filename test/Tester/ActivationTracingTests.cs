using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.General
{
    /// <summary>
    /// Failing test demonstrating missing activation tracing spans.
    /// Expects an activation Activity to be created on first grain activation.
    /// </summary>
    public class ActivationTracingTests : OrleansTestingBase, IClassFixture<ActivationTracingTests.Fixture>
    {
        private const string ActivationSourceName = "Microsoft.Orleans.Runtime.Activation";
        private static readonly ConcurrentBag<Activity> Started = new();

        static ActivationTracingTests()
        {
            var listener = new ActivityListener
            {
                ShouldListenTo = src => src.Name == ActivationSourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData,
                SampleUsingParentId = (ref ActivityCreationOptions<string> options) => ActivitySamplingResult.AllData,
                ActivityStarted = activity => Started.Add(activity),
            };
            ActivitySource.AddActivityListener(listener);
        }

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.ConfigureHostConfiguration(TestDefaultConfiguration.ConfigureHostConfiguration);
                builder.AddSiloBuilderConfigurator<SiloCfg>();
                builder.AddClientBuilderConfigurator<ClientCfg>();
            }

            private class SiloCfg : ISiloConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)
                {
                    hostBuilder
                        .AddActivityPropagation()
                        .AddMemoryGrainStorageAsDefault()
                        .AddMemoryGrainStorage("PubSubStore");
                }
            }

            private class ClientCfg : IClientBuilderConfigurator
            {
                public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
                {
                    clientBuilder.AddActivityPropagation();
                }
            }
        }

        private readonly Fixture _fixture;

        public ActivationTracingTests(Fixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        [TestCategory("BVT")]
        public async Task ActivationSpanIsCreatedOnFirstCall()
        {
            Started.Clear();

            using var parent = new Activity("test-parent");
            parent.Start();
            try
            {
                var grain = _fixture.GrainFactory.GetGrain<IActivityGrain>(Random.Shared.Next());
                // First call should force activation
                var _ = await grain.GetActivityId();

                // Expect at least one activation-related activity
                var activationActivities = Started.Where(a => a.Source.Name == ActivationSourceName).ToList();
                Assert.True(activationActivities.Count > 0, "Expected activation tracing activity to be created, but none were observed.");
            }
            finally
            {
                parent.Stop();
            }
        }
    }
}
