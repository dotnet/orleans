using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Orleans.Dashboard;
using Orleans.TestingHost;
using TestGrains;
using Orleans.Hosting;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json.Linq;
using Orleans.Dashboard.Implementation.Helpers;
using Orleans.Dashboard.Core;

namespace UnitTests
{
    public class GrainStateTests : IDisposable
    {
        private readonly TestCluster _cluster;
        public GrainStateTests()
        {
            var builder = new TestClusterBuilder();
            builder.AddSiloBuilderConfigurator<TestSiloConfigurations>();
            _cluster = builder.Build();
            _cluster.Deploy();
        }

        public void Dispose()
        {
            _cluster.StopAllSilos();
        }

        [Fact]
        public async Task TestGetGrainsTypes()
        {
            var dashboardGrain = _cluster.GrainFactory.GetGrain<IDashboardGrain>(1);
            var types = await dashboardGrain.GetGrainTypes();

            Assert.Contains("TestGrains.TestStateInMemoryGrain", types.Value);
        }

        [Fact]
        public async Task TestWithGetStateMethod()
        {
            var dashboardGrain = _cluster.GrainFactory.GetGrain<IDashboardGrain>(1);
            var stateGrain = _cluster.GrainFactory.GetGrain<ITestStateInMemoryGrain>(123);

            var immutableState = await dashboardGrain.GetGrainState("123", "TestGrains.TestStateInMemoryGrain");

            dynamic state = JObject.Parse(immutableState.Value);

            var stateFromGrain = await stateGrain.GetState();
            int counter = state.GetState.Counter;
            Assert.Equal(stateFromGrain.Counter, counter);
        }

        [Fact]
        public async Task TestWithIStorageField()
        {
            var dashboardGrain = _cluster.GrainFactory.GetGrain<IDashboardGrain>(1);
            var stateGrain = _cluster.GrainFactory.GetGrain<ITestStateGrain>(123);
            await stateGrain.WriteCounterState(new CounterState
            {
                Counter = 5,
                CurrentDateTime = DateTime.UtcNow
            });
            var immutableState = await dashboardGrain.GetGrainState("123", "TestGrains.TestStateGrain");

            dynamic state = JObject.Parse(immutableState.Value);

            var stateFromGrain = await stateGrain.GetCounterState();
            int counter = state.GetCounterState.Counter;
            Assert.Equal(stateFromGrain.Counter, counter);
        }


        public class TestSiloConfigurations : ISiloConfigurator
        {
            public void Configure(ISiloBuilder siloBuilder)
            {
                siloBuilder.UseInMemoryReminderService();
                siloBuilder.AddMemoryGrainStorageAsDefault();

                siloBuilder.Services.AddOrleansDashboardForSiloCore();
            }
        }
    }
}
