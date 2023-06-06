using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.ReminderService;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.TestHelper;
using Xunit;
using Xunit.Sdk;

namespace UnitTests.General
{
    public class ConsistentRingProviderTests_Silo : TestClusterPerTest
    {
        private const int numAdditionalSilos = 3;
        private readonly TimeSpan failureTimeout = TimeSpan.FromSeconds(30);
        private readonly TimeSpan endWait = TimeSpan.FromMinutes(5);

        private enum Fail { First, Random, Last }
        
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddSiloBuilderConfigurator<Configurator>();
            builder.AddClientBuilderConfigurator<Configurator>();
        }

        private class Configurator : ISiloConfigurator, IClientBuilderConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.AddMemoryGrainStorage("MemoryStore")
                    .AddMemoryGrainStorageAsDefault()
                    .UseInMemoryReminderService();
            }

            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder.Configure<GatewayOptions>(
                    options => options.GatewayListRefreshPeriod = TimeSpan.FromMilliseconds(100));
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Ring")]
        public async Task Ring_Basic()
        {
            await HostedCluster.StartAdditionalSilosAsync(numAdditionalSilos);
            await HostedCluster.WaitForLivenessToStabilizeAsync();
            VerificationScenario(0);
        }

        [Fact, TestCategory("Functional"), TestCategory("Ring")]
        public async Task Ring_1F_Random() => await FailureTest(Fail.Random, 1);

        [Fact, TestCategory("Functional"), TestCategory("Ring")]
        public async Task Ring_1F_Beginning() => await FailureTest(Fail.First, 1);

        [Fact, TestCategory("Functional"), TestCategory("Ring")]
        public async Task Ring_1F_End() => await FailureTest(Fail.Last, 1);

        [Fact, TestCategory("Functional"), TestCategory("Ring")]
        public async Task Ring_2F_Random() => await FailureTest(Fail.Random, 2);

        [Fact, TestCategory("Functional"), TestCategory("Ring")]
        public async Task Ring_2F_Beginning() => await FailureTest(Fail.First, 2);

        [Fact, TestCategory("Functional"), TestCategory("Ring")]
        public async Task Ring_2F_End() => await FailureTest(Fail.Last, 2);

        private async Task FailureTest(Fail failCode, int numOfFailures)
        {
            await HostedCluster.StartAdditionalSilosAsync(numAdditionalSilos);
            await HostedCluster.WaitForLivenessToStabilizeAsync();

            var failures = await getSilosToFail(failCode, numOfFailures);
            foreach (var fail in failures) // verify before failure
            {
                VerificationScenario(PickKey(fail.SiloAddress)); // fail.SiloAddress.GetConsistentHashCode());
            }

            logger.LogInformation(
                "FailureTest {FailureCount}, Code {FailureCode}, Stopping silos: {SiloAddresses}",
                numOfFailures,
                failCode,
                Utils.EnumerableToString(failures, handle => handle.SiloAddress.ToString()));
            var keysToTest = new List<uint>();
            foreach (var fail in failures) // verify before failure
            {
                keysToTest.Add(PickKey(fail.SiloAddress)); //fail.SiloAddress.GetConsistentHashCode());
                await HostedCluster.StopSiloAsync(fail);
            }
            await HostedCluster.WaitForLivenessToStabilizeAsync();

            AssertEventually(() =>
            {
                foreach (var key in keysToTest) // verify after failure
                {
                    VerificationScenario(key);
                }
            }, failureTimeout);
        }

        [Fact, TestCategory("Functional"), TestCategory("Ring")]
        public async Task Ring_1J() => await JoinTest(1);

        [Fact, TestCategory("Functional"), TestCategory("Ring")]
        public async Task Ring_2J() => await JoinTest(2);

        private async Task JoinTest(int numOfJoins)
        {
            logger.LogInformation("JoinTest {NumOfJoins}", numOfJoins);
            await HostedCluster.StartAdditionalSilosAsync(numAdditionalSilos - numOfJoins);
            await HostedCluster.WaitForLivenessToStabilizeAsync();

            var silos = await HostedCluster.StartAdditionalSilosAsync(numOfJoins);
            await HostedCluster.WaitForLivenessToStabilizeAsync();
            foreach (var sh in silos)
            {
                VerificationScenario(PickKey(sh.SiloAddress));
            }
            Thread.Sleep(TimeSpan.FromSeconds(15));
        }

        [Fact, TestCategory("Functional"), TestCategory("Ring")]
        public async Task Ring_1F1J()
        {
            await HostedCluster.StartAdditionalSilosAsync(numAdditionalSilos);
            await HostedCluster.WaitForLivenessToStabilizeAsync();
            var failures = await getSilosToFail(Fail.Random, 1);
            var keyToCheck = PickKey(failures[0].SiloAddress);// failures[0].SiloAddress.GetConsistentHashCode();
            List<SiloHandle> joins = null;

            // kill a silo and join a new one in parallel
            logger.LogInformation("Killing silo {SiloAddress} and joining a silo", failures[0].SiloAddress);
            
            var tasks = new Task[2]
            {
                Task.Factory.StartNew(() => HostedCluster.StopSiloAsync(failures[0])),
                HostedCluster.StartAdditionalSilosAsync(1).ContinueWith(t => joins = t.GetAwaiter().GetResult())
            };
            Task.WaitAll(tasks, endWait);

            await HostedCluster.WaitForLivenessToStabilizeAsync();

            AssertEventually(() =>
            {
                VerificationScenario(keyToCheck); // verify failed silo's key
                VerificationScenario(PickKey(joins[0].SiloAddress)); // verify newly joined silo's key
            }, failureTimeout);
        }

        // failing the secondary in this scenario exposed the bug in DomainGrain ... so, we keep it as a separate test than Ring_1F1J
        [Fact, TestCategory("Functional"), TestCategory("Ring")]
        public async Task Ring_1Fsec1J()
        {
            await HostedCluster.StartAdditionalSilosAsync(numAdditionalSilos);
            await HostedCluster.WaitForLivenessToStabilizeAsync();
            //List<SiloHandle> failures = getSilosToFail(Fail.Random, 1);
            var fail = HostedCluster.SecondarySilos.First();
            var keyToCheck = PickKey(fail.SiloAddress); //fail.SiloAddress.GetConsistentHashCode();
            List<SiloHandle> joins = null;

            // kill a silo and join a new one in parallel
            logger.LogInformation("Killing secondary silo {SiloAddress} and joining a silo", fail.SiloAddress);
            var tasks = new Task[2]
            {
                Task.Factory.StartNew(() => HostedCluster.StopSiloAsync(fail)),
                HostedCluster.StartAdditionalSilosAsync(1).ContinueWith(t => joins = t.GetAwaiter().GetResult())
            };
            Task.WaitAll(tasks, endWait);

            await HostedCluster.WaitForLivenessToStabilizeAsync();

            AssertEventually(() =>
            {
                VerificationScenario(keyToCheck); // verify failed silo's key
                VerificationScenario(PickKey(joins[0].SiloAddress));
            }, failureTimeout);
        }

        private uint PickKey(SiloAddress responsibleSilo)
        {
            var iteration = 10000;
            var testHooks = Client.GetTestHooks(HostedCluster.Primary);
            for (var i = 0; i < iteration; i++)
            {
                var next = Random.Shared.NextDouble();
                var randomKey = (uint)((double)RangeFactory.RING_SIZE * next);
                var s = testHooks.GetConsistentRingPrimaryTargetSilo(randomKey).Result;
                if (responsibleSilo.Equals(s))
                    return randomKey;
            }
            throw new Exception(string.Format("Could not pick a key that silo {0} will be responsible for. Primary.Ring = \n{1}",
                responsibleSilo, testHooks.GetConsistentRingProviderDiagnosticInfo().Result));
        }

        private void VerificationScenario(uint testKey)
        {
            // setup
            var silos = new List<SiloAddress>();

            foreach (var siloHandle in HostedCluster.GetActiveSilos())
            {
                long hash = siloHandle.SiloAddress.GetConsistentHashCode();
                var index = silos.FindLastIndex(siloAddr => siloAddr.GetConsistentHashCode() < hash) + 1;
                silos.Insert(index, siloHandle.SiloAddress);
            }

            // verify parameter key
            VerifyKey(testKey, silos);
            // verify some other keys as well, apart from the parameter key            
            // some random keys
            for (var i = 0; i < 3; i++)
            {
                VerifyKey((uint)Random.Shared.Next(), silos);
            }
            // lowest key
            var lowest = (uint)(silos.First().GetConsistentHashCode() - 1);
            VerifyKey(lowest, silos);
            // highest key
            var highest = (uint)(silos.Last().GetConsistentHashCode() + 1);
            VerifyKey(lowest, silos);
        }

        private void VerifyKey(uint key, List<SiloAddress> silos)
        {
            var testHooks = Client.GetTestHooks(HostedCluster.Primary);
            var truth = testHooks.GetConsistentRingPrimaryTargetSilo(key).Result; //expected;
            //if (truth == null) // if the truth isn't passed, we compute it here
            //{
            //    truth = silos.Find(siloAddr => (key <= siloAddr.GetConsistentHashCode()));
            //    if (truth == null)
            //    {
            //        truth = silos.First();
            //    }
            //}

            // lookup for 'key' should return 'truth' on all silos
            foreach (var siloHandle in HostedCluster.GetActiveSilos()) // do this for each silo
            {
                testHooks = Client.GetTestHooks(siloHandle);
                var s = testHooks.GetConsistentRingPrimaryTargetSilo((uint)key).Result;
                Assert.Equal(truth, s);
            }
        }

        private async Task<List<SiloHandle>> getSilosToFail(Fail fail, int numOfFailures)
        {
            var failures = new List<SiloHandle>();
            var count = 0;

            // Figure out the primary directory partition and the silo hosting the ReminderTableGrain.
            var tableGrain = GrainFactory.GetGrain<IReminderTableGrain>(InMemoryReminderTable.ReminderTableGrainId);
            var tableGrainId = ((GrainReference)tableGrain).GrainId;

            // Ping the grain to make sure it is active.
            await tableGrain.ReadRows(tableGrainId);

            var reminderTableGrainPrimaryDirectoryAddress = (await TestUtils.GetDetailedGrainReport(HostedCluster.InternalGrainFactory, tableGrainId, HostedCluster.Primary)).PrimaryForGrain;
            // ask a detailed report from the directory partition owner, and get the actionvation addresses
            var address = (await TestUtils.GetDetailedGrainReport(HostedCluster.InternalGrainFactory, tableGrainId, HostedCluster.GetSiloForAddress(reminderTableGrainPrimaryDirectoryAddress))).LocalDirectoryActivationAddress;
            var reminderGrainActivation = address;

            var ids = new SortedList<int, SiloHandle>();
            foreach (var siloHandle in HostedCluster.GetActiveSilos())
            {
                var siloAddress = siloHandle.SiloAddress;
                if (siloAddress.Equals(HostedCluster.Primary.SiloAddress))
                {
                    continue;
                }
                // Don't fail primary directory partition and the silo hosting the ReminderTableGrain.
                if (siloAddress.Equals(reminderTableGrainPrimaryDirectoryAddress) || siloAddress.Equals(reminderGrainActivation.SiloAddress))
                {
                    continue;
                }
                ids.Add(siloHandle.SiloAddress.GetConsistentHashCode(), siloHandle);
            }

            int index;
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
                        var r = ids.Values[Random.Shared.Next(ids.Count)];
                        while (failures.Contains(r))
                        {
                            r = ids.Values[Random.Shared.Next(ids.Count)];
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
            var ids = new SortedList<int, SiloAddress>(numAdditionalSilos + 2);
            foreach (var siloHandle in HostedCluster.GetActiveSilos())
            {
                ids.Add(siloHandle.SiloAddress.GetConsistentHashCode(), siloHandle.SiloAddress);
            }
            logger.LogInformation("{Message} list of silos: ", msg);
            foreach (var id in ids.Keys.ToList())
            {
                logger.LogInformation("{From} -> {To}", ids[id], id);
            }
        }

        private static void AssertEventually(Action assertion, TimeSpan timeout) => AssertEventually(assertion, timeout, TimeSpan.FromMilliseconds(500));

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
                catch (XunitException)
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
    }
}
