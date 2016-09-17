using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.GrainDirectory;
using Orleans.Runtime;
using TestGrainInterfaces;
using Orleans.Runtime.Configuration;
using Xunit;
using Xunit.Abstractions;

// ReSharper disable InconsistentNaming

namespace Tests.GeoClusterTests
{
    // We need use ClientWrapper to load a client object in a new app domain. 
    // This allows us to create multiple clients that are connected to different silos.

    public class GlobalSingleInstanceClusterTests : TestingClusterHost
    {

        public GlobalSingleInstanceClusterTests(ITestOutputHelper output) : base(output)
        { }

        /// <summary>
        /// Run all tests on a small configuration (two clusters, one silo each, one client each)
        /// </summary>
        /// <returns></returns>
        [Fact, TestCategory("Functional"), TestCategory("GeoCluster")]
        public async Task All_Small()
        {
            await Setup_Clusters(false);
            numGrains = 600;
            await RunWithTimeout("IndependentCreation", 5000, IndependentCreation);
            await RunWithTimeout("CreationRace", 10000, CreationRace);
            await RunWithTimeout("ConflictResolution", 20000, ConflictResolution);
        }

        /// <summary>
        /// Run all tests on a larger configuration (two clusters with 3 or 4 silos, respectively, and two clients each)
        /// </summary>
        /// <returns></returns>
        [Fact, TestCategory("GeoCluster")]
        public async Task All_Large()
        {
            await Setup_Clusters(true);
            numGrains = 2000;
            await RunWithTimeout("IndependentCreation", 20000, IndependentCreation);
            await RunWithTimeout("CreationRace", 60000, CreationRace);
            await RunWithTimeout("ConflictResolution", 120000, ConflictResolution);
        }


        #region client wrappers

        public class ClientWrapper : ClientWrapperBase
        {
            public ClientWrapper(string name, int gatewayport, string clusterId, Action<ClientConfiguration> customizer) : base(name, gatewayport, clusterId, customizer)
            {
                systemManagement = GrainClient.GrainFactory.GetGrain<IManagementGrain>(RuntimeInterfaceConstants.SYSTEM_MANAGEMENT_ID);
            }

            public int CallGrain(int i)
            {
                var grainRef = GrainClient.GrainFactory.GetGrain<IClusterTestGrain>(i);
                Task<int> toWait = grainRef.SayHelloAsync();
                toWait.Wait();
                return toWait.Result;
            }

            public void InjectMultiClusterConf(params string[] args)
            {
                systemManagement.InjectMultiClusterConfiguration(args).Wait();
            }

            IManagementGrain systemManagement;
        }

        #endregion

        private Random random = new Random();

        private int numGrains;
        private string cluster0;
        private string cluster1;
        private ClientWrapper[] clients;

        private async Task Setup_Clusters(bool largesetup)
        {
            await RunWithTimeout("Setup_Clusters", largesetup ? 120000 : 60000, async () =>
            {
                // use a random global service id for testing purposes
                var globalserviceid = Guid.NewGuid();

                Action<ClusterConfiguration> configurationcustomizer = (ClusterConfiguration c) =>
                {
                    // run the retry process every 5 seconds to keep this test shorter
                    c.Globals.GlobalSingleInstanceRetryInterval = TimeSpan.FromSeconds(5);
                };

                // Create two clusters, each with a single silo.
                cluster0 = "cluster0";
                cluster1 = "cluster1";
                NewGeoCluster(globalserviceid, cluster0, largesetup ? 3 : 1, configurationcustomizer);
                NewGeoCluster(globalserviceid, cluster1, largesetup ? 4 : 1, configurationcustomizer);

                if (!largesetup)
                {
                    // Create one client per cluster
                    clients = new ClientWrapper[]
                    {
                       NewClient<ClientWrapper>(cluster0, 0),
                       NewClient<ClientWrapper>(cluster1, 0),
                    };
                }
                else
                {
                    clients = new ClientWrapper[]
                    {
                       NewClient<ClientWrapper>(cluster0, 0),
                       NewClient<ClientWrapper>(cluster1, 0),
                       NewClient<ClientWrapper>(cluster0, 1),
                       NewClient<ClientWrapper>(cluster1, 1),
                    };
                }
                await WaitForLivenessToStabilizeAsync();

                // Configure multicluster
                clients[0].InjectMultiClusterConf(cluster0, cluster1);
                await WaitForMultiClusterGossipToStabilizeAsync(false);
            });
        }


        #region Test creation of independent grains

        private Task IndependentCreation()
        {
            int offset = random.Next();

            int base_own0 = GetGrainsInClusterWithStatus(cluster0, GrainDirectoryEntryStatus.Owned).Count;
            int base_own1 = GetGrainsInClusterWithStatus(cluster1, GrainDirectoryEntryStatus.Owned).Count;
            int base_requested0 = GetGrainsInClusterWithStatus(cluster0, GrainDirectoryEntryStatus.RequestedOwnership).Count;
            int base_requested1 = GetGrainsInClusterWithStatus(cluster1, GrainDirectoryEntryStatus.RequestedOwnership).Count;
            int base_doubtful0 = GetGrainsInClusterWithStatus(cluster0, GrainDirectoryEntryStatus.Doubtful).Count;
            int base_doubtful1 = GetGrainsInClusterWithStatus(cluster1, GrainDirectoryEntryStatus.Doubtful).Count;

            WriteLog("Counts: Cluster 0 => Owned={0} Requested={1} Doubtful={2}", base_own0, base_requested0, base_doubtful0);
            WriteLog("Counts: Cluster 1 => Owned={0} Requested={1} Doubtful={2}", base_own1, base_requested1, base_doubtful1);

            WriteLog("Starting parallel creation of {0} grains", numGrains);

            // Create grains on both clusters, using clients round-robin.
            Parallel.For(0, numGrains, paralleloptions, i =>
             {
                 int val = clients[i % clients.Count()].CallGrain(offset + i);
                 Assert.Equal(1, val);
             });

            // We expect all requests to resolve, and all created activations are in state OWNED

            int own0 = GetGrainsInClusterWithStatus(cluster0, GrainDirectoryEntryStatus.Owned).Count;
            int own1 = GetGrainsInClusterWithStatus(cluster1, GrainDirectoryEntryStatus.Owned).Count;
            int doubtful0 = GetGrainsInClusterWithStatus(cluster0, GrainDirectoryEntryStatus.Doubtful).Count;
            int doubtful1 = GetGrainsInClusterWithStatus(cluster1, GrainDirectoryEntryStatus.Doubtful).Count;
            int requested0 = GetGrainsInClusterWithStatus(cluster0, GrainDirectoryEntryStatus.RequestedOwnership).Count;
            int requested1 = GetGrainsInClusterWithStatus(cluster1, GrainDirectoryEntryStatus.RequestedOwnership).Count;

            WriteLog("Counts: Cluster 0 => Owned={0} Requested={1} Doubtful={2}", own0, requested0, doubtful0);
            WriteLog("Counts: Cluster 1 => Owned={0} Requested={1} Doubtful={2}", own1, requested1, doubtful1);

            // Assert that all grains are in owned state
            Assert.Equal(numGrains / 2, own0 - base_own0);
            Assert.Equal(numGrains / 2, own1 - base_own1);
            Assert.Equal(doubtful0, base_doubtful0);
            Assert.Equal(doubtful1, base_doubtful1);
            Assert.Equal(requested0, base_requested0);
            Assert.Equal(requested1, base_requested1);

            return TaskDone.Done;
        }

        #endregion

        #region Creation Race

        // This test is for the case where two different clusters are racing, 
        // trying to activate the same grain.   

        // This function takes two arguments, a list of client configurations, and an integer. The list of client configurations is used to
        // create multiple clients that concurrently call the grains in range [0, numGrains). We run the experiment in a series of barriers.
        // The clients all invoke grain "g", in parallel, and then wait on a signal by the main thread (this function). The main thread, then 
        // wakes up the clients, after which they invoke "g+1", and so on.

        private Task CreationRace()
        {
            WriteLog("Starting ConcurrentCreation");

            var offset = random.Next();

            // take inventory now so we can exclude pre-existing entries from the validation
            var baseline = GetGrainActivations();

            // We use two objects to coordinate client threads and the main thread. coordWakeup is an object that is used to signal the coordinator
            // thread. toWait is used to signal client threads.
            var coordWakeup = new object();
            var toWait = new object();

            // We keep a list of client threads.
            var clientThreads = new List<Tuple<Thread, ClientThreadArgs>>();
            var rand = new Random();
            var results = new List<Tuple<int, int>>[clients.Length];
            threadsDone = results.Length;

            int index = 0;

            // Create a client thread corresponding to each client
            // The client thread will execute ThreadFunc function.
            foreach (var client in clients)
            {
                // A client thread takes a list of tupes<int, int> as argument. The list is an ordered sequence of grains to invoke. tuple.item2
                // is the grainId. tuple.item1 is never used (this should probably be cleaned up, but I don't want to break anything :).
                var args = new List<Tuple<int, int>>();
                for (int j = 0; j < numGrains; ++j)
                {
                    var waitTime = rand.Next(16, 100);
                    args.Add(Tuple.Create(waitTime, j));
                }

                // Given a config file, create client starts a client in a new appdomain. We also create a thread on which the client will run.
                // The thread takes a "ClientThreadArgs" as argument.
                var thread = new Thread(ThreadFunc);
                var threadFuncArgs = new ClientThreadArgs
                {
                    client = client,
                    args = args,
                    resultIndex = index,
                    numThreads = clients.Length,
                    coordWakeup = coordWakeup,
                    toWait = toWait,
                    results = results,
                    offset = offset
                };
                clientThreads.Add(Tuple.Create(thread, threadFuncArgs));
                index += 1;
            }

            // Go through the list of client threads, and start each of the threads with the appropriate arguments.
            foreach (var threadNArg in clientThreads)
            {
                var thread = threadNArg.Item1;
                var arg = threadNArg.Item2;

                thread.Start(arg);
            }

            // We run numGrains iterations of the experiment. The coordinator thread calls the function "WaitForWorkers" in order to wait
            // for the client threads to finish concurrent calls to a single grain. 
            for (int i = 0; i < numGrains; ++i)
            {
                WaitForWorkers(clients.Length, coordWakeup, toWait);
            }

            // Once the clients threads have finished calling the grain the appropriate number of times, we wait for them to write out their results.
            foreach (var threadNArg in clientThreads)
            {
                var thread = threadNArg.Item1;
                thread.Join();
            }

            var grains = GetGrainActivations(baseline);

            ValidateClusterRaceResults(results, grains);

            return TaskDone.Done;
        }

        private volatile int threadsDone;

        private void ValidateClusterRaceResults(List<Tuple<int, int>>[] results, Dictionary<GrainId, List<IActivationInfo>> grains)
        {
            WriteLog("Validating cluster race results");

            // there should be the right number of grains
            AssertEqual(numGrains, grains.Count, "number of grains in directory does not match");

            // each grain should have one activation per cluster
            foreach (var kvp in grains)
            {
                GrainId key = kvp.Key;
                List<IActivationInfo> activations = kvp.Value;

                Action error = () =>
                {
                    Assert.True(false, string.Format("grain {0} has wrong activations {1}",
                        key, string.Join(",", activations.Select(x =>
                            string.Format("{0}={1}", x.SiloAddress, x.RegistrationStatus)))));
                };

                // each grain has one activation per cluster
                if (activations.Count != 2)
                    error();

                // one should be owned and the other cached
                switch (activations[0].RegistrationStatus)
                {
                    case GrainDirectoryEntryStatus.Owned:
                        if (activations[1].RegistrationStatus != GrainDirectoryEntryStatus.Cached)
                            error();
                        break;
                    case GrainDirectoryEntryStatus.Cached:
                        if (activations[1].RegistrationStatus != GrainDirectoryEntryStatus.Owned)
                            error();
                        break;
                    default:
                        error();
                        break;
                }
            }

            // For each of the results that get, ensure that we see a sequence of values.

            foreach (var list in results)
                Assert.Equal(numGrains, list.Count);

            for (int i = 0; i < numGrains; ++i)
            {
                var vals = new List<int>();

                foreach (var list in results)
                    vals.Add(list[i].Item2);

                vals.Sort();

                for (int x = 0; x < results.Length; x++)
                    AssertEqual(x + 1, vals[x], "expect sequence of results, but got " + string.Join(",", vals));
            }
        }

        #endregion Creation Race

        #region Conflict Resolution

        // This test is used to test the case where two different clusters are racing, 
        // trying to activate the same grain, but inter-cluster communication is blocked
        // so they both activate an instance
        // and one of them deactivated once communication is unblocked

        public async Task ConflictResolution()
        {
            int offset = random.Next();

            int base_own0 = GetGrainsInClusterWithStatus(cluster0, GrainDirectoryEntryStatus.Owned).Count;
            int base_own1 = GetGrainsInClusterWithStatus(cluster1, GrainDirectoryEntryStatus.Owned).Count;
            int base_requested0 = GetGrainsInClusterWithStatus(cluster0, GrainDirectoryEntryStatus.RequestedOwnership).Count;
            int base_requested1 = GetGrainsInClusterWithStatus(cluster1, GrainDirectoryEntryStatus.RequestedOwnership).Count;
            int base_doubtful0 = GetGrainsInClusterWithStatus(cluster0, GrainDirectoryEntryStatus.Doubtful).Count;
            int base_doubtful1 = GetGrainsInClusterWithStatus(cluster1, GrainDirectoryEntryStatus.Doubtful).Count;
            int base_cached0 = GetGrainsInClusterWithStatus(cluster0, GrainDirectoryEntryStatus.Cached).Count;
            int base_cached1 = GetGrainsInClusterWithStatus(cluster1, GrainDirectoryEntryStatus.Cached).Count;

            WriteLog("Counts: Cluster 0 => Owned={0} Requested={1} Doubtful={2} Cached={3}", base_own0, base_requested0, base_doubtful0, base_cached0);
            WriteLog("Counts: Cluster 1 => Owned={0} Requested={1} Doubtful={2} Cached={3}", base_own1, base_requested1, base_doubtful1, base_cached1);

            // take inventory now so we can exclude pre-existing entries from the validation
            var baseline = GetGrainActivations();

            // Turn off intercluster messaging to simulate a partition.
            BlockAllClusterCommunication(cluster0, cluster1);
            BlockAllClusterCommunication(cluster1, cluster0);

            WriteLog("Starting creation of {0} grains on isolated clusters", numGrains);

            Parallel.For(0, numGrains, paralleloptions, i =>
            {
                int res0, res1, res2, res3;

                if (i % 2 == 1)
                {
                    res0 = clients[0].CallGrain(offset + i);
                    res1 = clients[1].CallGrain(offset + i);
                    res2 = clients[2 % clients.Length].CallGrain(offset + i);
                    res3 = clients[3 % clients.Length].CallGrain(offset + i);
                }
                else
                {
                    res0 = clients[1].CallGrain(offset + i);
                    res1 = clients[0].CallGrain(offset + i);
                    res2 = clients[0].CallGrain(offset + i);
                    res3 = clients[1].CallGrain(offset + i);
                }

                Assert.Equal(1, res0);
                Assert.Equal(1, res1);
                Assert.Equal(2, res2);
                Assert.Equal(2, res3);
            });

            // Validate that all the created grains are in DOUBTFUL, one activation in each cluster.
            Assert.True(GetGrainsInClusterWithStatus(cluster0, GrainDirectoryEntryStatus.Doubtful).Count == numGrains);
            Assert.True(GetGrainsInClusterWithStatus(cluster1, GrainDirectoryEntryStatus.Doubtful).Count == numGrains);

            WriteLog("Restoring inter-cluster communication");

            // Turn on intercluster messaging and wait for the resolution to kick in.
            UnblockAllClusterCommunication(cluster0);
            UnblockAllClusterCommunication(cluster1);

            // Wait for anti-entropy to kick in. 
            // One of the DOUBTFUL activations must be killed, and the other must be converted to OWNED.
            await Task.Delay(TimeSpan.FromSeconds(7));

            WriteLog("Validation of conflict resolution");

            int own0 = GetGrainsInClusterWithStatus(cluster0, GrainDirectoryEntryStatus.Owned).Count;
            int own1 = GetGrainsInClusterWithStatus(cluster1, GrainDirectoryEntryStatus.Owned).Count;
            int doubtful0 = GetGrainsInClusterWithStatus(cluster0, GrainDirectoryEntryStatus.Doubtful).Count;
            int doubtful1 = GetGrainsInClusterWithStatus(cluster1, GrainDirectoryEntryStatus.Doubtful).Count;
            int requested0 = GetGrainsInClusterWithStatus(cluster0, GrainDirectoryEntryStatus.RequestedOwnership).Count;
            int requested1 = GetGrainsInClusterWithStatus(cluster1, GrainDirectoryEntryStatus.RequestedOwnership).Count;
            int cached0 = GetGrainsInClusterWithStatus(cluster0, GrainDirectoryEntryStatus.Cached).Count;
            int cached1 = GetGrainsInClusterWithStatus(cluster1, GrainDirectoryEntryStatus.Cached).Count;

            WriteLog("Counts: Cluster 0 => Owned={0} Requested={1} Doubtful={2} Cached={3}", own0, requested0, doubtful0, cached0);
            WriteLog("Counts: Cluster 1 => Owned={0} Requested={1} Doubtful={2} Cached={3}", own1, requested1, doubtful1, cached1);

            AssertEqual(numGrains + base_own0 + base_own1, own0 + own1, "Expecting All are now Owned");
            AssertEqual(numGrains, cached0 + cached1 - base_cached0 - base_cached1, "Expecting All Owned have a cached in the other cluster");
            AssertEqual(0, doubtful0 + doubtful1 - base_doubtful0 - base_doubtful1, "Expecting No Doubtful");
            Assert.Equal(requested0, base_requested0);
            Assert.Equal(requested1, base_requested1);

            // each grain should have one activation per cluster
            var grains = GetGrainActivations(baseline);
            foreach (var kvp in grains)
            {
                GrainId key = kvp.Key;
                List<IActivationInfo> activations = kvp.Value;

                Action error = () =>
                {
                    Assert.True(false, string.Format("grain {0} has wrong activations {1}",
                        key, string.Join(",", activations.Select(x =>
                            string.Format("{0}={1}", x.SiloAddress, x.RegistrationStatus)))));
                };

                // each grain has one activation per cluster
                if (activations.Count != 2)
                    error();

                // one should be owned and the other cached
                switch (activations[0].RegistrationStatus)
                {
                    case GrainDirectoryEntryStatus.Owned:
                        if (activations[1].RegistrationStatus != GrainDirectoryEntryStatus.Cached)
                            error();
                        break;
                    case GrainDirectoryEntryStatus.Cached:
                        if (activations[1].RegistrationStatus != GrainDirectoryEntryStatus.Owned)
                            error();
                        break;
                    default:
                        error();
                        break;
                }
            }

            // ensure that the grain whose DOUBTFUL activation was killed,
            // now refers to the 'real' remote OWNED activation.
            for (int i = 0; i < numGrains; i++)
            {
                int res0, res1, res2, res3;
                if (i % 2 == 1)
                {
                    res0 = clients[0].CallGrain(offset + i);
                    res1 = clients[1].CallGrain(offset + i);
                    res2 = clients[2 % clients.Length].CallGrain(offset + i);
                    res3 = clients[3 % clients.Length].CallGrain(offset + i);
                }
                else
                {
                    res0 = clients[1].CallGrain(offset + i);
                    res1 = clients[0].CallGrain(offset + i);
                    res2 = clients[0].CallGrain(offset + i);
                    res3 = clients[1].CallGrain(offset + i);
                }
                //From the previous grain calls, the last value of the counter in each grain was 2.
                //So here should be sequenced from 3.
                Assert.Equal(3, res0);
                Assert.Equal(4, res1);
                Assert.Equal(5, res2);
                Assert.Equal(6, res3);
            }

        }
        #endregion

        #region Helper methods 

        private List<GrainId> GetGrainsInClusterWithStatus(string clusterId, GrainDirectoryEntryStatus? status = null)
        {
            List<GrainId> grains = new List<GrainId>();
            var silos = Clusters[clusterId].Silos;
            int totalSoFar = 0;
            foreach (var silo in silos)
            {
                var dir = silo.Silo.TestHook.GetDirectoryForTypeNamesContaining("ClusterTestGrain");
                foreach (var grainKeyValue in dir)
                {
                    GrainId grainId = grainKeyValue.Key;
                    IGrainInfo grainInfo = grainKeyValue.Value;
                    ActivationId actId = grainInfo.Instances.First().Key;
                    IActivationInfo actInfo = grainInfo.Instances[actId];

                    if (grainId.IsSystemTarget || grainId.IsClient || !grainId.IsGrain)
                    {
                        // Skip system grains, system targets and clients
                        // which never go through cluster-single-instance registration process
                        continue;
                    }

                    if (!status.HasValue || actInfo.RegistrationStatus == status)
                    {
                        grains.Add(grainId);
                    }
                }
                WriteLog("Returning: Silo {0} State = {1} Count = {2}", silo.Silo.SiloAddress, status.HasValue ? status.Value.ToString() : "ANY", (grains.Count - totalSoFar));
                totalSoFar = grains.Count;
            }
            WriteLog("Returning: Cluster {0} State = {1} Count = {2}", clusterId, status.HasValue ? status.Value.ToString() : "ANY", grains.Count);
            return grains;
        }

        private Dictionary<GrainId, List<IActivationInfo>> GetGrainActivations(Dictionary<GrainId, List<IActivationInfo>> exclude = null)
        {
            var grains = new Dictionary<GrainId, List<IActivationInfo>>();

            int instancecount = 0;

            foreach (var kvp in Clusters)
                foreach (var silo in kvp.Value.Silos)
                {
                    var dir = silo.Silo.TestHook.GetDirectoryForTypeNamesContaining("ClusterTestGrain");

                    foreach (var grainKeyValue in dir)
                    {
                        GrainId grainId = grainKeyValue.Key;
                        IGrainInfo grainInfo = grainKeyValue.Value;

                        if (exclude != null && exclude.ContainsKey(grainId))
                            continue;

                        List<IActivationInfo> acts;
                        if (!grains.TryGetValue(grainId, out acts))
                            grains[grainId] = acts = new List<IActivationInfo>();

                        foreach (var instinfo in grainInfo.Instances)
                        {
                            acts.Add(instinfo.Value);
                            instancecount++;
                        }
                    }
                }

            WriteLog("Returning: {0} instances for {1} grains", instancecount, grains.Count());

            return grains;
        }


        // This is a helper function which is used to run the race condition tests. This function waits for all client threads trying to create the
        // same activation to finish. The last client thread to finish will wakeup the coordinator thread. 
        private void WaitForCoordinator(int numThreads, object coordWakeup, object toWait)
        {
            Monitor.Enter(coordWakeup);
            Monitor.Enter(toWait);

            threadsDone -= 1;
            if (threadsDone == 0)
            {
                Monitor.Pulse(coordWakeup);
            }

            Monitor.Exit(coordWakeup);
            Monitor.Wait(toWait);
            Monitor.Exit(toWait);
        }

        // This is a helper function which is used to signal the worker client threads to run another iteration of our concurrent experiment.
        private void WaitForWorkers(int numThreads, object coordWakeup, object toWait)
        {
            Monitor.Enter(coordWakeup);

            while (threadsDone != 0)
            {
                Monitor.Wait(coordWakeup);
            }

            threadsDone = numThreads;
            Monitor.Exit(coordWakeup);

            Monitor.Enter(toWait);
            Monitor.PulseAll(toWait);
            Monitor.Exit(toWait);
        }

        // ClientThreadArgs is a set of arguments which is used by a client thread which is concurrently running with other client threads. We
        // use client threads in order to simulate race conditions.
        private class ClientThreadArgs
        {
            public ClientWrapper client;
            public IEnumerable<Tuple<int, int>> args;
            public int resultIndex;
            public int numThreads;
            public object coordWakeup;
            public object toWait;
            public List<Tuple<int, int>>[] results;
            public int offset;
        }

        // Each client thread which is concurrently trying to create a sequence of grains with other clients runs this function.
        private void ThreadFunc(object obj)
        {
            var threadArg = (ClientThreadArgs)obj;
            var resultList = new List<Tuple<int, int>>();

            // Go through the sequence of arguments one by one.
            foreach (var arg in threadArg.args)
            {
                try
                {
                    // Call the appropriate grain.
                    var grainId = arg.Item2;
                    int ret = threadArg.client.CallGrain(threadArg.offset + grainId);

                    Debug.WriteLine("*** Result = {0}", ret);

                    // Keep the result in resultList.
                    resultList.Add(Tuple.Create(grainId, ret));
                }
                catch (Exception e)
                {
                    WriteLog("Caught exception: {0}", e);
                }

                // Finally, wait for the coordinator to kick-off another round of the test.
                WaitForCoordinator(threadArg.numThreads, threadArg.coordWakeup, threadArg.toWait);
            }

            // Track the results for validation.
            lock (threadArg.results)
            {
                threadArg.results[threadArg.resultIndex] = resultList;
            }
        }






    }
    #endregion
}
