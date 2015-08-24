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

namespace Tester
{
    [TestClass]
    [DeploymentItem("OrleansConfigurationForPerSiloGrainTesting.xml")]
    [DeploymentItem("ClientConfigurationForTesting.xml")]
    [DeploymentItem("TestGrainInterfaces.dll")]
    [DeploymentItem("TestGrains.dll")]
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

        public PerSiloExampleGrainTests()
            : base(siloOptions, clientOptions)
        { }

        [TestInitialize]
        public void TestInitialize()
        {
            NumSilos = GetActiveSilos().Count();
        }

        [ClassCleanup]
        public static void MyClassCleanup()
        {
            StopAllSilos();
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("PerSilo")]
        public async Task PerSiloGrainExample_Init()
        {
            // This tests just bootstraps the 2 default test silos, and checks that partition grains were created on each.
            
            IPartitionManager partitionManager = GrainClient.GrainFactory.GetGrain<IPartitionManager>(0);
            IList<PartitionInfo> partitionInfos = await partitionManager.GetPartitionInfos();

            Assert.AreEqual(NumSilos, partitionInfos.Count, " PartitionInfo list should return {0} values.", NumSilos);
            Assert.AreNotEqual(partitionInfos[0].PartitionId, partitionInfos[1].PartitionId, "PartitionIds should be different.");
        }
    }
}
