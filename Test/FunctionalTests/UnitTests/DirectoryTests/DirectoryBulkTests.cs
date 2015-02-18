using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Runtime.MembershipService;
using System.Threading;
using Orleans;
using Orleans.Runtime;
using System.Diagnostics;

namespace UnitTests.DirectoryTests
{
    [TestClass]
    public class DirectoryBulkTests : UnitTestBase
    {
        const int N = 1000;
        public DirectoryBulkTests()
            : base(new Options {})
        { 

        }

        [TestMethod, TestCategory("Directory"), TestCategory("Revisit")]
        public void Dir_BulkSimpleVerificationScenario1()
        {
            // 1. Start Silos
            this.StartAdditionalOrleansRuntimes(5);
            WaitForLivenessToStabilize();
            // 2. Create activations
            var grains = CreateTestGrains(N);
            // 4. Scenario
            BulkVerificationScenario(grains, false);
        }



        [TestMethod, TestCategory("Directory"), TestCategory("Revisit")]
        public void Dir_BulkSimpleVerificationScenario2()
        {
            // 1. Start Silos
            this.StartAdditionalOrleansRuntimes(5);
            WaitForLivenessToStabilize();
            // 2. Create activations
            var grains = CreateTestGrains(N);
            // 4. Scenario
            BulkVerificationScenario(grains, true);
        }

        [TestMethod, TestCategory("Directory"), TestCategory("Revisit")]
        public void Dir_BulkSimpleVerificationScenario3()
        {
            // 1. Start Silos
            this.StartAdditionalOrleansRuntimes(5);
            WaitForLivenessToStabilize();
            // 2. Create activations
            var grains = CreateTestGrains(N);
            // 3. Wait for dir to replicate

            // 4. Scenario
            BulkVerificationScenario(grains, true, true);
        }

        [TestMethod, TestCategory("Directory"), TestCategory("Revisit")]
        public void Dir_BulkBackupVerificationScenario1()
        {
            // 1. Start Silos
            this.StartAdditionalOrleansRuntimes(5);
            WaitForLivenessToStabilize();
            // 2. Create activations
            var grains = CreateTestGrains(N);
            // 4. Scenario
            BulkVerificationScenario(grains, false, false, 0);
        }



        [TestMethod, TestCategory("Directory"), TestCategory("Revisit")]
        public void Dir_BulkBackupVerificationScenario2()
        {
            // 1. Start Silos
            this.StartAdditionalOrleansRuntimes(5);
            WaitForLivenessToStabilize();
            // 2. Create activations
            var grains = CreateTestGrains(N);
            // 3. Wait for dir to replicate

            // 4. Scenario
            BulkVerificationScenario(grains, false, true, 0);
        }

        [TestMethod, TestCategory("Directory"), TestCategory("Revisit")]
        public void Dir_BulkDoubleVerificationScenario1()
        {
            // 1. Start Silos
            this.StartAdditionalOrleansRuntimes(5);
            WaitForLivenessToStabilize();
            // 2. Create activations
            var grains = CreateTestGrains(N);
            // 3. Wait for dir to replicate

            // 4. Scenario
            BulkVerificationScenario(grains, true, false, 0);
        }

        [TestMethod, TestCategory("Directory"), TestCategory("Revisit")]
        public void Dir_BulkDoubleVerificationScenario2()
        {
            // 1. Start Silos
            this.StartAdditionalOrleansRuntimes(5);
            WaitForLivenessToStabilize();
            // 2. Create activations
            var grains = CreateTestGrains(N);
            // 3. Wait for dir to replicate

            // 4. Scenario
            BulkVerificationScenario(grains, true, false, 1);
        }

        [TestMethod, TestCategory("Directory"), TestCategory("Revisit")]
        public void Dir_BulkDoubleVerificationScenario3()
        {
            // 1. Start Silos
            this.StartAdditionalOrleansRuntimes(5);
            WaitForLivenessToStabilize();
            // 2. Create activations
            var grains = CreateTestGrains(N);
            // 3. Wait for dir to replicate

            // 4. Scenario
            BulkVerificationScenario(grains, false, false, 0, 1);
        }
        
        internal void BulkVerificationScenario(List<GrainId> grains, bool stopPrimary, bool shouldKill = false, params int[] backupsToKill )
        {
            var placements = GetGrainPlacements(grains);
            SiloAddress primaryAddress = placements.Keys.First();
            SiloHandle primary = GetSiloForAddress(primaryAddress);

            ValidatePrimaryForGrainOnAllSilos(grains);
            
            List<SiloHandle> runtimesToKill = new List<SiloHandle>();
            foreach (int i in backupsToKill)
            {
                var backup = GetSiloForAddress(primary.Silo.LocalGrainDirectory.GetSilosHoldingDirectoryInformationForGrain(placements[primaryAddress].First())[i]);
                runtimesToKill.Add(backup);
            }
            if (stopPrimary)
            {
                runtimesToKill.Add(primary);
            }
            foreach (var silo in runtimesToKill)
            {
                BounceRuntime(shouldKill, silo);
            }
            WaitForLivenessToStabilize();
            Thread.Sleep(TimeSpan.FromSeconds(60));
            ValidatePrimaryForGrainOnAllSilos(grains);
        }

        private static void BounceRuntime(bool shouldKill, SiloHandle primaryForGrain)
        {
            if (shouldKill)
            {
                KillRuntime(primaryForGrain);
            }
            else
            {
                RestartRuntime(primaryForGrain);
            }
        }
        private static Dictionary<SiloAddress, List<GrainId>> GetGrainPlacements(List<GrainId> grains)
        {
            Dictionary<SiloAddress, List<GrainId>> placements = new Dictionary<SiloAddress, List<GrainId>>();
            foreach (var grainId in grains) // for each grain that we created.
            {
                var primary = Primary.Silo.LocalGrainDirectory.GetPrimaryForGrain(grainId);
                if (!placements.ContainsKey(primary))
                {
                    placements.Add(primary, new List<GrainId>());
                }
                placements[primary].Add(grainId);
            }
            return placements;
        }
        private List<GrainId> CreateTestGrains(int n)
        {
            List<GrainId> grains = new List<GrainId>();
            // 2. Create activation
            //for (int i = 0; i < n; i++)
            //{
            //    var g = SimpleOrleansManagedGrainFactory.CreateGrain(i, new[] { GrainStrategy.PartitionPlacement(i) });
            //    g.Wait();
            //    GrainReference grain = (GrainReference)g;
            //    grains.Add(grain.GrainId);
            //}
            //Thread.Sleep(TimeSpan.FromSeconds(60));
            return grains;
        }
        private void ValidatePrimaryForGrainOnAllSilos(IEnumerable<GrainId> grainIds)
        {
            foreach (var siloHandle in GetActiveSilos()) // do this for each silo
            {
                foreach (var grainId in grainIds)
                {
                    // 4. Find primary for grain
                    var primary = siloHandle.Silo.LocalGrainDirectory.GetPrimaryForGrain(grainId);

                    // 4 A - on the primary silo, look up - which should find the grain and return true
                    List<ActivationAddress> primaryFullList;
                    bool isFoundOnPrimary = GetSiloForAddress(primary).Silo.LocalGrainDirectory.LocalLookup(grainId, out primaryFullList);
                    Assert.IsTrue(isFoundOnPrimary, "Inconsistent {0} believes {1} to be Primary for {2} when it is not", siloHandle.Silo.SiloAddress, primary, grainId);

                    // 5. Find backups
                    var backups = siloHandle.Silo.LocalGrainDirectory.GetSilosHoldingDirectoryInformationForGrain(grainId);
                    foreach (var backup in backups)
                    {
                        Assert.AreNotEqual(primary, backup, "The primary and backup can't have same address.");

                        bool isFoundOnBackup;
                        List<ActivationAddress> backupFullList = GetSiloForAddress(backup).Silo.LocalGrainDirectory.GetLocalDataForGrain(grainId, out isFoundOnBackup);
                        Assert.IsFalse(isFoundOnBackup, "Inconsistent {0} believes {1} to be Primary for {2} when it should not", siloHandle.Silo.SiloAddress, primary, grainId);

                        // 6. Verify that the backups have the same data
                        if ((primaryFullList.Count > 0) || (backupFullList.Count > 0))
                        {
                            CollectionAssert.AreEqual(primaryFullList, backupFullList,
                                    "Backup on silo {0} has a different list than primary on silo {1}: ({2}) expected, ({3}) found", backup, primary, primaryFullList.ToStrings(),
                                    backupFullList.ToStrings());
                        }
                    }
                }
            }
        }
    }
}
