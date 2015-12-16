using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;

namespace UnitTests.General
{
    [TestClass]
    public class GenericGrainsInAzureStorageTests : UnitTestSiloHost
    {
        public GenericGrainsInAzureStorageTests()
            : base(new TestingSiloOptions
            {
                StartPrimary = true,
                StartSecondary = false,
                AdjustConfig = config =>
                {
                    const string myProviderFullTypeName = "Orleans.Storage.AzureTableStorage";
                    const string myProviderName = "AzureStore";
                    var properties = new Dictionary<string, string>();
                    properties.Add("DataConnectionString", "UseDevelopmentStorage=true");
                    config.Globals.RegisterStorageProvider(myProviderFullTypeName, myProviderName, properties);
                }
            })
        {
        }

        [ClassCleanup]
        public static void MyClassCleanup()
        {
            StopAllSilos();
        }

        [TestMethod, TestCategory("Azure"), TestCategory("Functional"), TestCategory("Generics")]
        [Ignore]
        //This test currently fails, because the name of the interface is too long
        public async Task Generic_OnAzureTableStorage_LongNamedGrain_EchoValue()
        {
            var grain = GrainFactory.GetGrain<ISimpleGenericGrainUsingAzureTableStorage<int>>(Guid.NewGuid());
            await grain.EchoAsync(42);

            //ClearState() also exhibits the error, even with the shorter named grain
            //await grain.ClearState();
        }

        [TestMethod, TestCategory("Azure"), TestCategory("Functional"), TestCategory("Generics")]
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
