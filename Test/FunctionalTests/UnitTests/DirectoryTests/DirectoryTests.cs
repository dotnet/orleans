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
    public class DirectoryTests : UnitTestBase
    {
        public DirectoryTests()
            : base()
        { 

        }

        [TestMethod, TestCategory("Directory"), TestCategory("Revisit")]
        public void Dir_SimpleVerificationScenario1()
        {
            // 1. Start Silos
            this.StartAdditionalOrleansRuntimes(5);
            WaitForLivenessToStabilize();
            // 2. Create activations
            var grains = CreateTestGrains(1);
            // 4. Scenario
            SimpleVerificationScenario(grains, false);
        }

        [TestMethod, TestCategory("Directory"), TestCategory("Revisit")]
        public void Dir_SimpleVerificationScenario2()
        {
            // 1. Start Silos
            this.StartAdditionalOrleansRuntimes(5);
            WaitForLivenessToStabilize();
            // 2. Create activations
            var grains = CreateTestGrains(1);
            // 4. Scenario
            SimpleVerificationScenario(grains, true);
        }

        [TestMethod, TestCategory("Directory"), TestCategory("Revisit")]
        public void Dir_SimpleVerificationScenario3()
        {
            // 1. Start Silos
            this.StartAdditionalOrleansRuntimes(5);
            WaitForLivenessToStabilize();
            // 2. Create activations
            var grains = CreateTestGrains(1);
            // 3. Wait for dir to replicate

            // 4. Scenario
            SimpleVerificationScenario(grains, true, true);
        }

        [TestMethod, TestCategory("Directory"), TestCategory("Revisit")]
        public void Dir_BackupVerificationScenario1()
        {
            // 1. Start Silos
            this.StartAdditionalOrleansRuntimes(5);
            WaitForLivenessToStabilize();
            // 2. Create activations
            var grains = CreateTestGrains(1);
            // 4. Scenario
            BackupVerificationScenario(grains, 0, false);
        }

        [TestMethod, TestCategory("Directory"), TestCategory("Revisit")]
        public void Dir_BackupVerificationScenario2()
        {
            // 1. Start Silos
            this.StartAdditionalOrleansRuntimes(5);
            WaitForLivenessToStabilize();
            // 2. Create activations
            var grains = CreateTestGrains(1);
            // 3. Wait for dir to replicate

            // 4. Scenario
            BackupVerificationScenario(grains, 0, true);
        }

        [TestMethod, TestCategory("Directory"), TestCategory("Revisit")]
        public void Dir_DoubleVerificationScenario1()
        {
            // 1. Start Silos
            this.StartAdditionalOrleansRuntimes(5);
            WaitForLivenessToStabilize();
            // 2. Create activations
            var grains = CreateTestGrains(1);
            // 3. Wait for dir to replicate

            // 4. Scenario
            BackupBatchVerificationScenario(grains, true, false, 0);
        }

        [TestMethod, TestCategory("Directory"), TestCategory("Revisit")]
        public void Dir_DoubleVerificationScenario2()
        {
            // 1. Start Silos
            this.StartAdditionalOrleansRuntimes(5);
            WaitForLivenessToStabilize();
            // 2. Create activations
            var grains = CreateTestGrains(1);
            // 3. Wait for dir to replicate

            // 4. Scenario
            BackupBatchVerificationScenario(grains, true, false, 1);
        }

        [TestMethod, TestCategory("Directory"), TestCategory("Revisit")]
        public void Dir_DoubleVerificationScenario3()
        {
            // 1. Start Silos
            this.StartAdditionalOrleansRuntimes(5);
            WaitForLivenessToStabilize();
            // 2. Create activations
            var grains = CreateTestGrains(1);
            // 3. Wait for dir to replicate

            // 4. Scenario
            BackupBatchVerificationScenario(grains, false, false, 0, 1);
        }

        internal void SimpleVerificationScenario(List<GrainId> grains, bool stopPrimary, bool shouldKill = false)
        {
            foreach (var grainId in grains) // for each grain that we created.
            {
                ValidatePrimaryForGrainOnAllSilos(grains);
                if (stopPrimary)
                {
                    var primaryForGrain = GetSiloForAddress(Primary.Silo.LocalGrainDirectory.GetPrimaryForGrain(grainId));
                    BounceRuntime(shouldKill, primaryForGrain);
                    WaitForLivenessToStabilize();
                    Thread.Sleep(TimeSpan.FromSeconds(60));
                    ValidatePrimaryForGrainOnAllSilos(grains);
                }
            }
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

        internal void BackupVerificationScenario(List<GrainId> grains, int nTh =0, bool shouldKill = false)
        {
            foreach (var grainId in grains) // for each grain that we created.
            {
                ValidatePrimaryForGrainOnAllSilos(grains);
                var primaryForGrain = GetSiloForAddress(Primary.Silo.LocalGrainDirectory.GetPrimaryForGrain(grainId));
                var backup = GetSiloForAddress(primaryForGrain.Silo.LocalGrainDirectory.GetSilosHoldingDirectoryInformationForGrain(grainId).Skip(nTh).First());
                BounceRuntime(shouldKill, backup);
                WaitForLivenessToStabilize();
                Thread.Sleep(TimeSpan.FromSeconds(60));
                ValidatePrimaryForGrainOnAllSilos(grains);
            }
        }

        internal void BackupBatchVerificationScenario(List<GrainId> grains, bool stopPrimary, bool shouldKill = false, params int[] backupsToKill )
        {
            
            foreach (var grainId in grains) // for each grain that we created.
            {
                ValidatePrimaryForGrainOnAllSilos(grains);
                var primaryForGrain = GetSiloForAddress(Primary.Silo.LocalGrainDirectory.GetPrimaryForGrain(grainId));
                
                foreach (int i in backupsToKill)
                {
                    var backup = GetSiloForAddress(primaryForGrain.Silo.LocalGrainDirectory.GetSilosHoldingDirectoryInformationForGrain(grainId)[i]);
                    BounceRuntime(shouldKill, backup);
                }
                if (stopPrimary)
                {
                    BounceRuntime(shouldKill, primaryForGrain);
                }
                WaitForLivenessToStabilize();
                Thread.Sleep(TimeSpan.FromSeconds(60));
                ValidatePrimaryForGrainOnAllSilos(grains);
            }
        }

        private List<GrainId> CreateTestGrains(int n)
        {
            List<GrainId> grains = new List<GrainId>();
            //// 2. Create activation
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
