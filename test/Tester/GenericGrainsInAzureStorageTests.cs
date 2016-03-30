using System;
using System.Threading.Tasks;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using Tester;
using Xunit;

namespace UnitTests.General
{
    public class GenericGrainsInAzureStorageTests : OrleansTestingBase, IClassFixture<GenericGrainsInAzureStorageTests.Fixture>
    {
        private class Fixture : BaseClusterFixture
        {
            protected override TestingSiloHost CreateClusterHost()
            {
                return new TestingSiloHost(new TestingSiloOptions
                {
                    StartPrimary = true,
                    StartSecondary = false,
                    AdjustConfig = config =>
                    {
                        config.AddAzureTableStorageProvider("AzureStore", StorageTestConstants.DataConnectionString);
                    }
                });
            }
        }

        [Fact(Skip = "Ignored"), TestCategory("Azure"), TestCategory("Functional"), TestCategory("Generics")]
        //This test currently fails, because the name of the interface is too long
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
