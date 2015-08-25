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

using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.TestingHost;
using TestGrainInterfaces;
using UnitTests.Tester;
using System;
using Orleans.Runtime;
using TestGrains;

namespace Tester
{
    [TestClass]
    [DeploymentItem("OrleansConfigurationForPerSiloGrainTesting.xml")]
    public class PerSiloExampleGrainTests : UnitTestSiloHost
    {
        private static readonly TestingSiloOptions siloOptions = new TestingSiloOptions
        {
            SiloConfigFile = new FileInfo("OrleansConfigurationForPerSiloGrainTesting.xml"),
            PropagateActivityId = true
        };

        private static readonly TestingClientOptions clientOptions = new TestingClientOptions
        {
            ClientConfigFile = new FileInfo("ClientConfigurationForTesting.xml"),
            PropagateActivityId = true
        };

        private int NumSilos { get; set; }
        private IManagementGrain mgmtGrain; 

        public PerSiloExampleGrainTests()
            : base(siloOptions, clientOptions)
        { }

        [TestInitialize]
        public void TestInitialize()
        {
            NumSilos = GetActiveSilos().Count();
            mgmtGrain = GrainFactory.GetGrain<IManagementGrain>(RuntimeInterfaceConstants.SYSTEM_MANAGEMENT_ID);
        }

        [TestCleanup]
        public void MyTestCleanup()
        {
            StopAllSilos();
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("PerSilo")]
        public async Task Init_PerSiloGrainExample()
        {
            // This tests just bootstraps the 2 default test silos, and checks that partition grains were created on each.
            
            IPartitionManager partitionManager = GrainClient.GrainFactory.GetGrain<IPartitionManager>(0);
            IList<PartitionInfo> partitionInfos = await partitionManager.GetPartitionInfos();

            Assert.AreEqual(NumSilos, partitionInfos.Count, " PartitionInfo list should return {0} values.", NumSilos);
            Assert.AreNotEqual(partitionInfos[0].PartitionId, partitionInfos[1].PartitionId, "PartitionIds should be different.");
            await CountActivations("Initial");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("PerSilo")]
        public async Task SendMsg_Client_PerSiloGrainExample()
        {
            IPartitionManager partitionManager = GrainClient.GrainFactory.GetGrain<IPartitionManager>(0);
            IList<PartitionInfo> partitionInfosList1 = await partitionManager.GetPartitionInfos();

            Assert.AreEqual(NumSilos, partitionInfosList1.Count, "Initial: PartitionInfo list should return {0} values.", NumSilos);
            Assert.AreNotEqual(partitionInfosList1[0].PartitionId, partitionInfosList1[1].PartitionId, "Initial: PartitionIds should be different.");
            await CountActivations("Initial");

            foreach (var partition in partitionInfosList1)
            {
                Guid partitionId = partition.PartitionId;
                IPartitionGrain grain = GrainFactory.GetGrain<IPartitionGrain>(partitionId);
                PartitionInfo pi = await grain.GetPartitionInfo();
            }

            await CountActivations("After Send");

            IList<PartitionInfo> partitionInfosList2 = await partitionManager.GetPartitionInfos();

            Assert.AreEqual(NumSilos, partitionInfosList2.Count, "After Send: PartitionInfo list should return {0} values.", NumSilos);
            foreach (int i in new [] {0, 1})
            {
                Assert.AreEqual(partitionInfosList1[i].PartitionId, partitionInfosList2[i].PartitionId, "After Send: Same PartitionIds [{0}]", i);
            }
            Assert.AreNotEqual(partitionInfosList2[0].PartitionId, partitionInfosList2[1].PartitionId, "After Send: PartitionIds should be different.");
            await CountActivations("After checks");
        }

        private async Task CountActivations(string when)
        {
            string grainType = typeof(PartitionGrain).FullName;
            int siloCount = NumSilos;
            int expectedGrainsPerSilo = 1;
            var grainStats = (await mgmtGrain.GetSimpleGrainStatistics()).ToList();
            Console.WriteLine("Got All Grain Stats: " + string.Join(" ", grainStats));
            var partitionGrains = grainStats.Where(gs => gs.GrainType == grainType).ToList();
            Console.WriteLine("Got PartitionGrain Stats: " + string.Join(" ", partitionGrains));
            var wrongSilos = partitionGrains.Where(gs => gs.ActivationCount != expectedGrainsPerSilo).ToList();
            Assert.AreEqual(0, wrongSilos.Count, when + ": Silos with wrong number of {0} grains: {1}",
                grainType, string.Join(" ", wrongSilos));
            int count = partitionGrains.Select(gs => { return gs.ActivationCount; }).Sum();
            Assert.AreEqual(siloCount, count, when + ": Total count of {0} grains should be {1}. Got: {2}",
                grainType, siloCount, string.Join(" ", grainStats));
        }
    }
}
