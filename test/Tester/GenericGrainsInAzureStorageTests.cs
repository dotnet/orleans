using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime.Configuration;
using Orleans.Storage;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using Tester;
using Xunit;

namespace UnitTests.General
{
    public class GenericGrainsInAzureStorageTests : OrleansTestingBase, IClassFixture<GenericGrainsInAzureStorageTests.Fixture>
    {
        private class Fixture : BaseTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions();
                options.ClusterConfiguration.AddAzureTableStorageProvider("AzureStore");
                return new TestCluster(options);
            }
        }

        [Fact, TestCategory("Azure"), TestCategory("Functional"), TestCategory("Generics")]
        public async Task Generic_OnAzureTableStorage_LongNamedGrain_EchoValue()
        {
            var grain = GrainFactory.GetGrain<ISimpleGenericGrainUsingAzureTableStorage<int>>(Guid.NewGuid());
            await grain.EchoAsync(42);

            //ClearState() also exhibits the error, even with the shorter named grain
            //await grain.ClearState();
        }

        [Fact, TestCategory("Azure"), TestCategory("Functional"), TestCategory("Generics")]
        //This test is identical to the one above, with a shorter name, and passes
        public async Task Generic_OnAzureTableStorage_ShortNamedGrain_EchoValue()
        {
            var grain = GrainFactory.GetGrain<ITinyNameGrain<int>>(Guid.NewGuid());
            await grain.EchoAsync(42);

            //ClearState() also exhibits the error, even with the shorter named grain
            //await grain.ClearState();
        }
    }
}
