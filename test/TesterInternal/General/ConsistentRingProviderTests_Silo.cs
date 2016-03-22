﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Orleans;
using Orleans.GrainDirectory;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using Xunit;
using UnitTests.Tester;

namespace UnitTests.General
{
    public class ConsistentRingProviderTests_Silo : HostedTestClusterPerTest
    {
        private static readonly TestingSiloOptions siloOptions = new TestingSiloOptions
        {
            StartFreshOrleans = true,
            ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain,
            LivenessType = GlobalConfiguration.LivenessProviderType.MembershipTableGrain,
            SiloConfigFile = new FileInfo("OrleansConfigurationForTesting.xml"),
        };

        private const int numAdditionalSilos = 3;
        private readonly TimeSpan failureTimeout = TimeSpan.FromSeconds(30);
        private readonly TimeSpan endWait = TimeSpan.FromMinutes(5);

        enum Fail { First, Random, Last }

        public override TestingSiloHost CreateSiloHost()
        {
            return new TestingSiloHost(siloOptions);
        }

        #region Tests

        [Fact, TestCategory("Functional"), TestCategory("Ring")]
        public async Task Ring_Basic()
        {
            this.HostedCluster.StartAdditionalSilos(numAdditionalSilos);
            await this.HostedCluster.WaitForLivenessToStabilizeAsync();
            VerificationScenario(0);
        }

        [Fact, TestCategory("Functional"), TestCategory("Ring")]
        public async Task Ring_1F_Random()
        {
            await FailureTest(Fail.Random, 1);
        }

        [Fact, TestCategory("Functional"), TestCategory("Ring")]
        public async Task Ring_1F_Beginning()
        {
            await FailureTest(Fail.First, 1);
        }

        [Fact, TestCategory("Functional"), TestCategory("Ring")]
        public async Task Ring_1F_End()
        {
            await FailureTest(Fail.Last, 1);
        }

        [Fact, TestCategory("Functional"), TestCategory("Ring")]
        public async Task Ring_2F_Random()
        {
            await FailureTest(Fail.Random, 2);
        }

        [Fact, TestCategory("Functional"), TestCategory("Ring")]
        public async Task Ring_2F_Beginning()
        {
            await FailureTest(Fail.First, 2);
        }

        [Fact, TestCategory("Functional"), TestCategory("Ring")]
        public async Task Ring_2F_End()
        {
            await FailureTest(Fail.Last, 2);
        }

        private async Task FailureTest(Fail failCode, int numOfFailures)
        {
            this.HostedCluster.StartAdditionalSilos(numAdditionalSilos);
            await this.HostedCluster.WaitForLivenessToStabilizeAsync();

            List<SiloHandle> failures = getSilosToFail(failCode, numOfFailures);
            foreach (SiloHandle fail in failures) // verify before failure
            {
                VerificationScenario(PickKey(fail.Silo.SiloAddress)); // fail.Silo.SiloAddress.GetConsistentHashCode());
            }

            logger.Info("FailureTest {0}, Code {1}, Stopping silos: {2}", numOfFailures, failCode, Utils.EnumerableToString(failures, handle => handle.Silo.SiloAddress.ToString()));
            List<uint> keysToTest = new List<uint>();
            foreach (SiloHandle fail in failures) // verify before failure
            {
                keysToTest.Add(PickKey(fail.Silo.SiloAddress)); //fail.Silo.SiloAddress.GetConsistentHashCode());
                this.HostedCluster.StopSilo(fail);
            }
            await this.HostedCluster.WaitForLivenessToStabilizeAsync();

            AssertEventually(() =>
            {
                foreach (var key in keysToTest) // verify after failure
                {
                    VerificationScenario(key);
                }
            }, failureTimeout);
        }

        [Fact, TestCategory("Functional"), TestCategory("Ring")]
        public async Task Ring_1J()
        {
            await JoinTest(1);
        }

        [Fact, TestCategory("Functional"), TestCategory("Ring")]
        public async Task Ring_2J()
        {
            await JoinTest(2);
        }

        private async Task JoinTest(int numOfJoins)
        {
            logger.Info("JoinTest {0}", numOfJoins);
            this.HostedCluster.StartAdditionalSilos(numAdditionalSilos - numOfJoins);
            await this.HostedCluster.WaitForLivenessToStabilizeAsync();

            List<SiloHandle> silos = this.HostedCluster.StartAdditionalSilos(numOfJoins);
            await this.HostedCluster.WaitForLivenessToStabilizeAsync();
            foreach (SiloHandle sh in silos)
            {
                VerificationScenario(PickKey(sh.Silo.SiloAddress)); 
            }
            Thread.Sleep(TimeSpan.FromSeconds(15));
        }

        [Fact, TestCategory("Functional"), TestCategory("Ring")]
        public async Task Ring_1F1J()
        {
            this.HostedCluster.StartAdditionalSilos(numAdditionalSilos);
            await this.HostedCluster.WaitForLivenessToStabilizeAsync();
            List<SiloHandle> failures = getSilosToFail(Fail.Random, 1);
            uint keyToCheck = PickKey(failures[0].Silo.SiloAddress);// failures[0].Silo.SiloAddress.GetConsistentHashCode();
            List<SiloHandle> joins = null;

            // kill a silo and join a new one in parallel
            logger.Info("Killing silo {0} and joining a silo", failures[0].Silo.SiloAddress);
            var tasks = new Task[2]
            {
                Task.Factory.StartNew(() => this.HostedCluster.StopSilo(failures[0])),
                Task.Factory.StartNew(() => joins = this.HostedCluster.StartAdditionalSilos(1))
            };
            Task.WaitAll(tasks, endWait);

            await this.HostedCluster.WaitForLivenessToStabilizeAsync();

            AssertEventually(() =>
            {
                VerificationScenario(keyToCheck); // verify failed silo's key
                VerificationScenario(PickKey(joins[0].Silo.SiloAddress)); // verify newly joined silo's key
            }, failureTimeout);
        }

        // failing the secondary in this scenario exposed the bug in DomainGrain ... so, we keep it as a separate test than Ring_1F1J
        [Fact, TestCategory("Functional"), TestCategory("Ring")]
        public async Task Ring_1Fsec1J()
        {
            this.HostedCluster.StartAdditionalSilos(numAdditionalSilos);
            await this.HostedCluster.WaitForLivenessToStabilizeAsync();
            //List<SiloHandle> failures = getSilosToFail(Fail.Random, 1);
            SiloHandle fail = this.HostedCluster.Secondary;
            uint keyToCheck = PickKey(fail.Silo.SiloAddress); //fail.Silo.SiloAddress.GetConsistentHashCode();
            List<SiloHandle> joins = null;

            // kill a silo and join a new one in parallel
            logger.Info("Killing secondary silo {0} and joining a silo", fail.Silo.SiloAddress);
            var tasks = new Task[2]
            {
                Task.Factory.StartNew(() => this.HostedCluster.StopSilo(fail)),
                Task.Factory.StartNew(() => joins = this.HostedCluster.StartAdditionalSilos(1))
            };
            Task.WaitAll(tasks, endWait);

            await this.HostedCluster.WaitForLivenessToStabilizeAsync();

            AssertEventually(() =>
            {
                VerificationScenario(keyToCheck); // verify failed silo's key
                VerificationScenario(PickKey(joins[0].Silo.SiloAddress));
            }, failureTimeout);
        }

        #endregion

        #region Utility methods

        private uint PickKey(SiloAddress responsibleSilo)
        {
            int iteration = 10000;
            for (int i = 0; i < iteration; i++)
            {
                double next = random.NextDouble();
                uint randomKey = (uint)((double)RangeFactory.RING_SIZE * next);
                SiloAddress s = this.HostedCluster.Primary.Silo.TestHook.ConsistentRingProvider.GetPrimaryTargetSilo(randomKey);
                if (responsibleSilo.Equals(s))
                    return randomKey;
            }
            throw new Exception(String.Format("Could not pick a key that silo {0} will be responsible for. Primary.Ring = \n{1}",
                responsibleSilo, this.HostedCluster.Primary.Silo.TestHook.ConsistentRingProvider));
        }

        private void VerificationScenario(uint testKey)
        {
            // setup
            List<SiloAddress> silos = new List<SiloAddress>();

            foreach (var siloHandle in this.HostedCluster.GetActiveSilos())
            {
                long hash = siloHandle.Silo.SiloAddress.GetConsistentHashCode();
                int index = silos.FindLastIndex(siloAddr => siloAddr.GetConsistentHashCode() < hash) + 1;
                silos.Insert(index, siloHandle.Silo.SiloAddress);
            }

            // verify parameter key
            VerifyKey(testKey, silos);
            // verify some other keys as well, apart from the parameter key            
            // some random keys
            for (int i = 0; i < 3; i++)
            {
                VerifyKey((uint)random.Next(), silos);
            }
            // lowest key
            uint lowest = (uint)(silos.First().GetConsistentHashCode() - 1);
            VerifyKey(lowest, silos);
            // highest key
            uint highest = (uint)(silos.Last().GetConsistentHashCode() + 1);
            VerifyKey(lowest, silos);
        }

        private void VerifyKey(uint key, List<SiloAddress> silos)
        {
            SiloAddress truth = this.HostedCluster.Primary.Silo.TestHook.ConsistentRingProvider.GetPrimaryTargetSilo(key); //expected;
            //if (truth == null) // if the truth isn't passed, we compute it here
            //{
            //    truth = silos.Find(siloAddr => (key <= siloAddr.GetConsistentHashCode()));
            //    if (truth == null)
            //    {
            //        truth = silos.First();
            //    }
            //}

            // lookup for 'key' should return 'truth' on all silos
            foreach (var siloHandle in this.HostedCluster.GetActiveSilos()) // do this for each silo
            {
                SiloAddress s = siloHandle.Silo.TestHook.ConsistentRingProvider.GetPrimaryTargetSilo((uint)key);
                Assert.AreEqual(truth, s, string.Format("Lookup wrong for key: {0} on silo: {1}", key, siloHandle.Silo.SiloAddress));
            }
        }

        private List<SiloHandle> getSilosToFail(Fail fail, int numOfFailures)
        {
            List<SiloHandle> failures = new List<SiloHandle>();
            int count = 0, index = 0;

            // Figure out the primary directory partition and the silo hosting the ReminderTableGrain.
            bool usingReminderGrain = this.HostedCluster.Primary.Silo.GlobalConfig.ReminderServiceType.Equals(GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain);
            IReminderTable tableGrain = GrainClient.GrainFactory.GetGrain<IReminderTableGrain>(Constants.ReminderTableGrainId);
            SiloAddress reminderTableGrainPrimaryDirectoryAddress = this.HostedCluster.Primary.Silo.LocalGrainDirectory.GetPrimaryForGrain(((GrainReference) tableGrain).GrainId);
            SiloHandle reminderTableGrainPrimaryDirectory = this.HostedCluster.GetActiveSilos().Where(sh => sh.Silo.SiloAddress.Equals(reminderTableGrainPrimaryDirectoryAddress)).FirstOrDefault();
            AddressesAndTag addresses;
            bool res = reminderTableGrainPrimaryDirectory.Silo.LocalGrainDirectory.LocalLookup(((GrainReference)tableGrain).GrainId, out addresses);
            ActivationAddress reminderGrainActivation = addresses.Addresses.FirstOrDefault();

            SortedList<int, SiloHandle> ids = new SortedList<int, SiloHandle>();
            foreach (var siloHandle in this.HostedCluster.GetActiveSilos())
            {
                SiloAddress siloAddress = siloHandle.Silo.SiloAddress;
                if (siloAddress.Equals(this.HostedCluster.Primary.Silo.SiloAddress))
                {
                    continue;
                }
                // Don't fail primary directory partition and the silo hosting the ReminderTableGrain.
                if (usingReminderGrain)
                {
                    if (siloAddress.Equals(reminderTableGrainPrimaryDirectoryAddress) || siloAddress.Equals(reminderGrainActivation.Silo))
                    {
                        continue;
                    }
                }
                ids.Add(siloHandle.Silo.SiloAddress.GetConsistentHashCode(), siloHandle);
            }

            // we should not fail the primary!
            // we can't guarantee semantics of 'Fail' if it evalutes to the primary's address
            switch (fail)
            {
                case Fail.First:
                    index = 0;
                    while (count++ < numOfFailures)
                    {
                        while (failures.Contains(ids.Values[index]))
                        {
                            index++;
                        }
                        failures.Add(ids.Values[index]);
                    }
                    break;
                case Fail.Last:
                    index = ids.Count - 1;
                    while (count++ < numOfFailures)
                    {
                        while (failures.Contains(ids.Values[index]))
                        {
                            index--;
                        }
                        failures.Add(ids.Values[index]);
                    }
                    break;
                case Fail.Random:
                default:
                    while (count++ < numOfFailures)
                    {
                        SiloHandle r = ids.Values[random.Next(ids.Count)];
                        while (failures.Contains(r))
                        {
                            r = ids.Values[random.Next(ids.Count)];
                        }
                        failures.Add(r);
                    }
                    break;
            }
            return failures;
        }

        // for debugging only
        private void printSilos(string msg)
        {
            SortedList<int, SiloAddress> ids = new SortedList<int, SiloAddress>(numAdditionalSilos + 2);
            foreach (var siloHandle in this.HostedCluster.GetActiveSilos())
            {
                ids.Add(siloHandle.Silo.SiloAddress.GetConsistentHashCode(), siloHandle.Silo.SiloAddress);
            }
            logger.Info("{0} list of silos: ", msg);
            foreach (var id in ids.Keys.ToList())
            {
                logger.Info("{0} -> {1}", ids[id], id);
            }
        }

        private static void AssertEventually(Action assertion, TimeSpan timeout)
        {
            AssertEventually(assertion, timeout, TimeSpan.FromMilliseconds(500));
        }

        private static void AssertEventually(Action assertion, TimeSpan timeout, TimeSpan delayBetweenIterations)
        {
            var sw = Stopwatch.StartNew();

            while (true)
            {
                try
                {
                    assertion();
                    return;
                }
                catch (Microsoft.VisualStudio.TestTools.UnitTesting.AssertFailedException)
                {
                    if (sw.ElapsedMilliseconds > timeout.TotalMilliseconds)
                    {
                        throw;
                    }
                }

                if (delayBetweenIterations > TimeSpan.Zero)
                {
                    Thread.Sleep(delayBetweenIterations);
                }
            }
        }

        #endregion

    }
}
