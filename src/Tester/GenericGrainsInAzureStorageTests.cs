/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

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
            : base(new TestingSiloOptions { StartPrimary = true, StartSecondary = false })
        {
        }
        
        [ClassCleanup]
        public static void MyClassCleanup()
        {
            StopAllSilos();
        }
        
        public override void AdjustForTest(ClusterConfiguration config)
        {
            const string myProviderFullTypeName = "Orleans.Storage.AzureTableStorage";
            const string myProviderName = "AzureStore";
            var properties = new Dictionary<string, string>();
            properties.Add("DataConnectionString", "UseDevelopmentStorage=true");
            config.Globals.RegisterStorageProvider(myProviderFullTypeName, myProviderName, properties);
            base.AdjustForTest(config);
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
