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
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Xunit.Abstractions;

// ReSharper disable InconsistentNaming

namespace Tests.GeoClusterTests
{
    // We need use ClientWrapper to load a client object in a new app domain. 
    // This allows us to create multiple clients that are connected to different silos.

    public class GlobalSingleInstanceClusterTests : TestingClusterHost, IDisposable
    {

        public GlobalSingleInstanceClusterTests(ITestOutputHelper output) : base(output)
        { }


        // Kill all clients and silos.
        public void Dispose()
        {
            WriteLog("Disposing...");
            var stopwatch = new System.Diagnostics.Stopwatch();
            stopwatch.Start();

            try
            {
                var disposetask = Task.Run(() => StopAllClientsAndClusters());

                disposetask.WaitWithThrow(TimeSpan.FromMinutes(System.Diagnostics.Debugger.IsAttached ? 60 : 2));
            }
            catch (Exception e)
            {
                WriteLog("Exception caught in test cleanup function: {0}", e);
                throw e;
            }

            stopwatch.Stop();
            WriteLog("Dispose completed (elapsed = {0}).", stopwatch.Elapsed);
        }

        #region client wrappers

        public class ClientWrapper : ClientWrapperBase
        {
            public ClientWrapper(string name, int gatewayport) : base(name, gatewayport)
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

        ParallelOptions paralleloptions = new ParallelOptions() { MaxDegreeOfParallelism = 4 };

        #region Creation of clusters and non-conflicting grains

        // This function is used to test the activation creation protocol. It runs with two clusters, with 1 silo each.
        // Takes about 1 minute.
        [Fact, TestCategory("GeoCluster"), TestCategory("Functional")]
        public async Task TestClusterCreation_1_1()
        {
            await RunWithTimeout("TestClusterCreation_1_1", 120000, async () =>
            {
                // use a random global service id for testing purposes
                var globalserviceid = Guid.NewGuid();

                // Create two clusters, each with a single silo.
                var cluster0 = "cluster0";
                var cluster1 = "cluster1";
                NewGeoCluster(globalserviceid, cluster0, 1);
                NewGeoCluster(globalserviceid, cluster1, 1);

                await WaitForLivenessToStabilizeAsync();

                // Create one client per cluster
                var client0 = NewClient<ClientWrapper>(cluster0, 0);
                var client1 = NewClient<ClientWrapper>(cluster1, 0);

                // Configure multicluster
                client0.InjectMultiClusterConf(cluster0, cluster1);
                await WaitForMultiClusterGossipToStabilizeAsync(false);

                int baseCount0 = GetGrainsInClusterWithStatus(cluster0, MultiClusterStatus.Owned).Count;
                int baseCount1 = GetGrainsInClusterWithStatus(cluster1, MultiClusterStatus.Owned).Count;
                int baseCount = baseCount0 + baseCount1;


                const int numGrains = 2000;

                WriteLog("Starting parallel creation of {0} grains", numGrains);

                // Create grains on both clusters. Alternating between the two.
                Parallel.For(0, numGrains, paralleloptions, i =>
                 {
                     int val;
                     if (i % 2 == 0)
                     {
                      // Make calls to even numbered grains using client0. Client0 is connected to cluster 0.
                      val = client0.CallGrain(i);
                     }
                     else
                     {
                      // Make calls to odd numbered grains using client1. Client1 is connected to cluster1.
                      val = client1.CallGrain(i);
                     }

                     Assert.AreEqual(1, val);
                 });

                // We expect all requests to resolve, and all created activations are in state OWNED

                // Ensure that we have the correct number of OWNED grains. 
                // We have created 2000 grains, 1000 grains are created on cluster 0, 
                // and 1000 grains are created on cluster1.
                // Get the grain directory associated with each of the clusters.
                int own0 = GetGrainsInClusterWithStatus(cluster0, MultiClusterStatus.Owned).Count;
                int own1 = GetGrainsInClusterWithStatus(cluster1, MultiClusterStatus.Owned).Count;
                int ownCount = own0 + own1;

                int doubt0 = GetGrainsInClusterWithStatus(cluster0, MultiClusterStatus.Doubtful).Count;
                int doubt1 = GetGrainsInClusterWithStatus(cluster1, MultiClusterStatus.Doubtful).Count;

                int req0 = GetGrainsInClusterWithStatus(cluster0, MultiClusterStatus.RequestedOwnership).Count;
                int req1 = GetGrainsInClusterWithStatus(cluster1, MultiClusterStatus.RequestedOwnership).Count;

                Console.WriteLine("Counts: Cluster 0 => Owned={0} Requested={1} Doubtful={2}", own0, req0, doubt0);
                Console.WriteLine("Counts: Cluster 1 => Owned={0} Requested={1} Doubtful={2}", own1, req1, doubt1);

                // Assert that the number of OWNED grains is equal to the number of grains that we invoked.
                Assert.AreEqual(numGrains + baseCount, ownCount);
            });
        }

        // This function is used to test the activation creation algorithm when two clusters create non-conflicting activations.
        // Takes around 1:45 min
        [Fact, TestCategory("GeoCluster")]
        public async Task TestClusterCreation_3_4()
        {
            await RunWithTimeout("TestClusterCreation_3_4", 240000, async () =>
            {    // use a random global service id for testing purposes
                var globalserviceid = Guid.NewGuid();

                // Create two clusters, each with 5 silos.
                var cluster0 = "cluster0";
                var cluster1 = "cluster1";
                NewGeoCluster(globalserviceid, cluster0, 3);
                NewGeoCluster(globalserviceid, cluster1, 4);

                await WaitForLivenessToStabilizeAsync();

                // Clients 0 and 1 connected to Cluster 0
                var client0 = NewClient<ClientWrapper>(cluster0, 0);
                var client1 = NewClient<ClientWrapper>(cluster0, 1);
                // Clients 2 and 3 connected to Cluster 1
                var client2 = NewClient<ClientWrapper>(cluster1, 0);
                var client3 = NewClient<ClientWrapper>(cluster1, 1);

                //Configure multicluster
                client3.InjectMultiClusterConf(cluster0, cluster1);
                await WaitForMultiClusterGossipToStabilizeAsync(false);

                int countsBase0 = GetGrainsInClusterWithStatus(cluster0, MultiClusterStatus.Owned).Count;
                int countsBase1 = GetGrainsInClusterWithStatus(cluster1, MultiClusterStatus.Owned).Count;
                int countsBase = countsBase0 + countsBase1;

                // Create 2000 grains, 1000 grains in each cluster. We alternate the calls among two clients connected to the same cluster. This allows
                // us to ensure that two clients within the same cluster never end up with two separate activations of the same grain.

                const int numGrains = 2000;

                WriteLog("Starting parallel creation of {0} grains", numGrains);

                // Ensure that we're running this test with an even number of grains :).
                Assert.AreEqual(0, numGrains % 2);

                Stopwatch sw = Stopwatch.StartNew();

                Parallel.For(0, numGrains, paralleloptions, i =>
                {
                    int first, second;
                    string pat; // Call pattern
                    if (i % 4 == 0)
                    {
                        first = client0.CallGrain(i);
                        second = client1.CallGrain(i);
                        pat = "0-1";
                    }
                    else if (i % 4 == 1)
                    {
                        first = client1.CallGrain(i);
                        second = client0.CallGrain(i);
                        pat = "1-0";
                    }
                    else if (i % 4 == 2)
                    {
                        first = client2.CallGrain(i);
                        second = client3.CallGrain(i);
                        pat = "2-3";
                    }
                    else
                    {
                        first = client3.CallGrain(i);
                        second = client2.CallGrain(i);
                        pat = "3-2";
                    }

                    // Make sure that the values we see are 1 and 2. 
                    // This means that two clients connected to silos in the same cluster 
                    // both called the same activation of the grain.

                    // TODO: Enable these checks requires the use of the confusingly mis-configured client #
                    Assert.AreEqual(1, first, "Value from first call to grain {0} with call pattern {1}", i, pat);
                    Assert.AreEqual(2, second, "Value from second call to grain {0} with call pattern {1}", i, pat);
                });
                sw.Stop();

                WriteLog("Elapsed={0}", sw.Elapsed);

                // Count the total number of OWNED activations in cluster0.
                var countsCluster0 = GetGrainsInClusterWithStatus(cluster0, MultiClusterStatus.Owned).Count;
                // Count the total number of OWNED activations in cluster1. 
                var countsCluster1 = GetGrainsInClusterWithStatus(cluster1, MultiClusterStatus.Owned).Count;

                // Check that total number of OWNED grains that we counted is equal to the number of grains that were activated.
                Assert.AreEqual(numGrains, countsCluster0 + countsCluster1 - countsBase,
                    "Total grains: c0={0} c1={1} base0={2} base1={3}",
                    countsCluster0, countsCluster1, countsBase0, countsBase1);

                // The grains are divided evenly among clusters0 and 1. Verify this.
                Assert.AreEqual(numGrains / 2, countsCluster0 - countsBase0,
                    "Cluster 0 grains: count={0} base={1}", countsCluster0, countsBase0);
                Assert.AreEqual(numGrains / 2, countsCluster1 - countsBase1,
                    "Cluster 1 grains: count={0} base={1}", countsCluster1, countsBase1);
            });
        }

        #endregion

        #region Race Conditions

        private volatile int threadsDone;


        // This function is used to test the case where two different clusters are racing, 
        // trying to activate the same grain.
        [Fact, TestCategory("GeoCluster"), TestCategory("Functional")]
        public async Task TestClusterRace_1_1()
        {
            await RunWithTimeout("TestClusterRace_1_1", 120000, async () =>
            {
                // use a random global service id for testing purposes
                var globalserviceid = Guid.NewGuid();
                Action<ClusterConfiguration> customizer = (ClusterConfiguration c) =>
                {
                    c.Globals.ServiceId = globalserviceid;
                };

                // Create two clusters, each with 1 silo. 
                var cluster0 = "cluster0";
                var cluster1 = "cluster1";
                NewGeoCluster(globalserviceid, cluster0, 1);
                NewGeoCluster(globalserviceid, cluster1, 1);

                await WaitForLivenessToStabilizeAsync();

                //Configure multicluster
                var cfgclient = NewClient<ClientWrapper>(cluster1, 0);
                cfgclient.InjectMultiClusterConf(cluster0, cluster1);
                await WaitForMultiClusterGossipToStabilizeAsync(false);

                // Create two clients, connect each client to the appropriate cluster.
                var clients = new List<ClientIdentity>
            {
                new ClientIdentity() { cluster = cluster0, number = 0 },
                new ClientIdentity() { cluster = cluster1, number = 0 },
            };

                const int numGrains = 2000;

                // We perform a run of concurrent experiments. 
                // we expect that all calls from concurrent clients will reference 
                // the same activation of a grain.
                var results = DoConcurrentExperiment(clients, numGrains);

                // validate the results and the directory
                ValidateClusterRaceResults(numGrains, results);
            });
        }

        // This test is exactly the same as TestClusterRace_1_1. 
        // The only difference is that we run each cluster with more than one silo, 
        // and also use multiple clients connected to silos in the same cluster. 
        // The structure of the experiment itself is identical to that of TestSingleSingleClusterRace.
        [Fact, TestCategory("GeoCluster")]
        public async Task TestClusterRace_3_4()
        {
            await RunWithTimeout("TestClusterRace_3_4", 180000, async () =>
            {
                // use a random global service id for testing purposes
                var globalserviceid = Guid.NewGuid();

                // Create two clusters, one with two and one with four silos.
                var cluster0 = "cluster0";
                var cluster1 = "cluster1";
                NewGeoCluster(globalserviceid, cluster0, 3);
                NewGeoCluster(globalserviceid, cluster1, 4);

                await WaitForLivenessToStabilizeAsync();

                //Configure multicluster
                var cfgclient = NewClient<ClientWrapper>(cluster1, 0);
                cfgclient.InjectMultiClusterConf(cluster0, cluster1);
                await WaitForMultiClusterGossipToStabilizeAsync(false);

                const int numGrains = 2000;

                // Create multiple clients. Two clients connect to each cluster.
                var clients = new List<ClientIdentity>
            {
                new ClientIdentity() { cluster = cluster0, number = 0 },
                new ClientIdentity() { cluster = cluster1, number = 0 },
                new ClientIdentity() { cluster = cluster0, number = 1 },
                new ClientIdentity() { cluster = cluster1, number = 1 },
            };

                // We perform a run of concurrent experiments. 
                // we expect that all calls from concurrent clients will reference 
                // the same activation of a grain.

                var results = DoConcurrentExperiment(clients, numGrains);

                ValidateClusterRaceResults(numGrains, results);
            });
        }

        private void ValidateClusterRaceResults(int numGrains, List<Tuple<int, int>>[] results)
        {
            WriteLog("Validating cluster race results");

            var grains = GetGrainActivations();

            // there should be the right number of grains
            Assert.AreEqual(numGrains, grains.Count, "number of grains in directory does not match");

            // each grain should have one activation per cluster
            foreach (var kvp in grains)
            {
                GrainId key = kvp.Key;
                List<IActivationInfo> activations = kvp.Value;

                Action error = () =>
                {
                    Assert.Fail("grain {0} has wrong activations {1}",
                        key, string.Join(",", activations.Select(x =>
                            string.Format("{0}={1}", x.SiloAddress, x.RegistrationStatus))));
                    Debugger.Break();
                };

                // each grain has one activation per cluster
                if (activations.Count != 2)
                    error();

                // one should be owned and the other cached
                switch (activations[0].RegistrationStatus)
                {
                    case MultiClusterStatus.Owned:
                        if (activations[1].RegistrationStatus != MultiClusterStatus.Cached)
                            error();
                        break;
                    case MultiClusterStatus.Cached:
                        if (activations[1].RegistrationStatus != MultiClusterStatus.Owned)
                            error();
                        break;
                    default:
                        error();
                        break;
                }
            }
       
            // For each of the results that get, ensure that we see a sequence of values.
            
            foreach (var list in results)
                Assert.AreEqual(numGrains, list.Count);

            for (int i = 0; i < numGrains; ++i)
            {
                var vals = new List<int>();
                
                foreach(var list in results)
                    vals.Add(list[i].Item2);

                vals.Sort();

                for (int x = 0; x < results.Length; x++)
                    Assert.AreEqual(x+1, vals[x], "expect sequence of results, but got " + string.Join(",",vals));
            }
        }

        #endregion Race Conditions

        #region Conflict Resolution

        // This function is used to test the anti-entropy protocol.
        [Fact, TestCategory("GeoCluster"), TestCategory("Functional")]
        public async Task TestConflictResolution_1_1()
        {
            await RunWithTimeout("TestConflictResolution_1_1", 120000, async () =>
            {
                // use a random global service id for testing purposes
                var globalserviceid = Guid.NewGuid();
                Action<ClusterConfiguration> configurationcustomizer = (ClusterConfiguration c) =>
                {
                    // run the retry process every 5 seconds to keep this test shorter
                    c.Globals.GlobalSingleInstanceRetryInterval = TimeSpan.FromSeconds(5);
                };

                // create two clusters with 1 silo each
                var cluster0 = "cluster0";
                var cluster1 = "cluster1";
                NewGeoCluster(globalserviceid, cluster0, 1, configurationcustomizer);
                NewGeoCluster(globalserviceid, cluster1, 1, configurationcustomizer);

                await WaitForLivenessToStabilizeAsync();

                var client0 = NewClient<ClientWrapper>(cluster0, 0);
                var client1 = NewClient<ClientWrapper>(cluster1, 0);

                //Configure multicluster
                client0.InjectMultiClusterConf(cluster0, cluster1);
                await WaitForMultiClusterGossipToStabilizeAsync(false);

                // Count the total number of already OWNED grain activations
                // in cluster0 (created during silo creation - like the Membership Grain).
                // These should be excluded from out test results;
                // we assume they don't dissapear before the test is over.
                int countsBase0 = GetGrainsInClusterWithStatus(cluster0, MultiClusterStatus.Owned).Count;
                int countsBase1 = GetGrainsInClusterWithStatus(cluster1, MultiClusterStatus.Owned).Count;

                // Turn off intercluster messaging to simulate a partition.
                BlockAllClusterCommunication(cluster0, cluster1);
                BlockAllClusterCommunication(cluster1, cluster0);

                const int numGrains = 10;

                WriteLog("Starting creation of {0} grains on isolated clusters", numGrains);

                // This should create two activations of each grain - one in each cluster.
                Parallel.For(0, numGrains, paralleloptions, i =>
                {
                    var res0 = client0.CallGrain(i);
                    var res1 = client1.CallGrain(i);

                    Assert.AreEqual(1, res0);
                    Assert.AreEqual(1, res1);
                });

                // Validate that all the created grains are DOUBTFUL, one activation in each cluster.
                Assert.IsTrue(GetGrainsInClusterWithStatus(cluster0, MultiClusterStatus.Doubtful).Count == numGrains, "c0 - Expecting All are Doubtful");
                Assert.IsTrue(GetGrainsInClusterWithStatus(cluster1, MultiClusterStatus.Doubtful).Count == numGrains, "c1 - Expecting All are Doubtful");


                WriteLog("Restoring inter-cluster communication");

                // un-block intercluster messaging.
                UnblockAllClusterCommunication(cluster0);
                UnblockAllClusterCommunication(cluster1);

                // Wait for anti-entropy to kick in. 
                // One of the DOUBTFUL activations must be killed, and the other must be converted to OWNED.
                await Task.Delay(TimeSpan.FromSeconds(7));

                WriteLog("Validation of conflict resolution");

                // Validate that all the duplicates have been resolved.
                var owned0 = GetGrainsInClusterWithStatus(cluster0, MultiClusterStatus.Owned).Count;
                var owned1 = GetGrainsInClusterWithStatus(cluster1, MultiClusterStatus.Owned).Count;
                var cached0 = GetGrainsInClusterWithStatus(cluster0, MultiClusterStatus.Cached).Count;
                var cached1 = GetGrainsInClusterWithStatus(cluster1, MultiClusterStatus.Cached).Count;
                var doubtful0 = GetGrainsInClusterWithStatus(cluster0, MultiClusterStatus.Doubtful).Count;
                var doubtful1 = GetGrainsInClusterWithStatus(cluster1, MultiClusterStatus.Doubtful).Count;

                Assert.IsTrue(owned0 + owned1 == numGrains + countsBase1 + countsBase0, "Expecting All are now Owned");
                Assert.IsTrue(cached0 + cached1 == numGrains, "Expecting All Owned have a cached in the other cluster");
                Assert.IsTrue(doubtful0 + doubtful1 == 0, "Expecting No Doubtful");

                // We need to ensure that the grain whose DOUBTFUL activation was killed,
                // and now refers to the 'real' remote OWNED activation.
                Parallel.For(0, numGrains, paralleloptions, i =>
                {
                    var res0 = client0.CallGrain(i);
                    var res1 = client1.CallGrain(i);

                    Assert.IsTrue(res0 == 2 && res1 == 3);
                });
            });
        }

        // This test is exactly the same as TestConflictResolution. The only difference is that we use more silos per cluster.
        [Fact, TestCategory("GeoCluster")]
        public async Task TestConflictResolution_3_4()
        {
            await RunWithTimeout("TestConflictResolution_3_4", 330000, async () =>
            {
                // use a random global service id for testing purposes
                var globalserviceid = Guid.NewGuid();
                Action<ClusterConfiguration> configurationcustomizer = (ClusterConfiguration c) =>
                {
                    // run the retry process every 5 seconds to keep this test shorter
                    c.Globals.GlobalSingleInstanceRetryInterval = TimeSpan.FromSeconds(5);
                };

                // create two clusters 
                var cluster0 = "cluster0";
                var cluster1 = "cluster1";
                NewGeoCluster(globalserviceid, cluster0, 3, configurationcustomizer);
                NewGeoCluster(globalserviceid, cluster1, 4, configurationcustomizer);

                await WaitForLivenessToStabilizeAsync();

                var client0 = NewClient<ClientWrapper>(cluster0, 0);
                var client1 = NewClient<ClientWrapper>(cluster1, 0);
                var client2 = NewClient<ClientWrapper>(cluster0, 1);
                var client3 = NewClient<ClientWrapper>(cluster1, 1);

                //Configure multicluster
                client2.InjectMultiClusterConf(cluster0, cluster1);
                await WaitForMultiClusterGossipToStabilizeAsync(false);

                ClientWrapper[] clients = { client0, client1, client2, client3 };

                // Count the total number of already OWNED grain activations
                // in cluster0 (created during silo creation - like the Membership Grain).
                // These should be excluded from out test results;
                // we assume they don't dissapear before the test is over.
                int countsBase0 = GetGrainsInClusterWithStatus(cluster0, MultiClusterStatus.Owned).Count;
                int countsBase1 = GetGrainsInClusterWithStatus(cluster1, MultiClusterStatus.Owned).Count;

                // Turn off intercluster messaging to simulate a partition.
                BlockAllClusterCommunication(cluster0, cluster1);
                BlockAllClusterCommunication(cluster1, cluster0);

                const int numGrains = 40;

                WriteLog("Starting creation of {0} grains on isolated clusters", numGrains);

                Parallel.For(0, numGrains, paralleloptions, i =>
                {
                    int res0, res1, res2, res3;
                    if (i % 2 == 1)
                    {
                        res0 = clients[0].CallGrain(i);
                        res1 = clients[1].CallGrain(i);
                        res2 = clients[2].CallGrain(i);
                        res3 = clients[3].CallGrain(i);
                    }
                    else
                    {
                        res0 = clients[1].CallGrain(i);
                        res1 = clients[0].CallGrain(i);
                        res2 = clients[0].CallGrain(i);
                        res3 = clients[1].CallGrain(i);
                    }

                    Assert.AreEqual(1, res0);
                    Assert.AreEqual(1, res1);
                    Assert.AreEqual(2, res2);
                    Assert.AreEqual(2, res3);
                });

                // Validate that all the created grains are in DOUBTFUL, one activation in each cluster.
                Assert.IsTrue(GetGrainsInClusterWithStatus(cluster0, MultiClusterStatus.Doubtful).Count == numGrains, "c0 - Expecting All are Doubtful");
                Assert.IsTrue(GetGrainsInClusterWithStatus(cluster1, MultiClusterStatus.Doubtful).Count == numGrains, "c1 - Expecting All are Doubtful");

                WriteLog("Restoring inter-cluster communication");

                // Turn on intercluster messaging and wait for the resolution to kick in.
                UnblockAllClusterCommunication(cluster0);
                UnblockAllClusterCommunication(cluster1);

                // Wait for anti-entropy to kick in. 
                // One of the DOUBTFUL activations must be killed, and the other must be converted to OWNED.
                await Task.Delay(TimeSpan.FromSeconds(7));

                WriteLog("Validation of conflict resolution");

                // Validate that all the duplicates have been resolved.
                var owned0 = GetGrainsInClusterWithStatus(cluster0, MultiClusterStatus.Owned).Count;
                var owned1 = GetGrainsInClusterWithStatus(cluster1, MultiClusterStatus.Owned).Count;
                var cached0 = GetGrainsInClusterWithStatus(cluster0, MultiClusterStatus.Cached).Count;
                var cached1 = GetGrainsInClusterWithStatus(cluster1, MultiClusterStatus.Cached).Count;
                var doubtful0 = GetGrainsInClusterWithStatus(cluster0, MultiClusterStatus.Doubtful).Count;
                var doubtful1 = GetGrainsInClusterWithStatus(cluster1, MultiClusterStatus.Doubtful).Count;

                Assert.IsTrue(owned0 + owned1 == numGrains + countsBase1 + countsBase0, "Expecting All are now Owned");
                Assert.IsTrue(cached0 + cached1 == numGrains, "Expecting All Owned have a cached in the other cluster");
                Assert.IsTrue(doubtful0 + doubtful1 == 0, "Expecting No Doubtful");

                // We need to ensure that the grain whose DOUBTFUL activation was killed,
                // and now refers to the 'real' remote OWNED activation.

                for (int i = 0; i < numGrains; i++)
                {
                    int res0, res1, res2, res3;
                    if (i % 2 == 1)
                    {
                        res0 = clients[0].CallGrain(i);
                        res1 = clients[1].CallGrain(i);
                        res2 = clients[2].CallGrain(i);
                        res3 = clients[3].CallGrain(i);
                    }
                    else
                    {
                        res0 = clients[1].CallGrain(i);
                        res1 = clients[0].CallGrain(i);
                        res2 = clients[0].CallGrain(i);
                        res3 = clients[1].CallGrain(i);
                    }
                    //From the previous grain calls, the last value of the counter in each grain was 2.
                    //So here should be sequenced from 3.
                    Assert.AreEqual(3, res0);
                    Assert.AreEqual(4, res1);
                    Assert.AreEqual(5, res2);
                    Assert.AreEqual(6, res3);
                }
            });
        }
        #endregion

        #region Private methods

        private void CompareClusterGrainCacheData(string cluster0, string cluster1, int clusterBase0, int clusterBase1)
        {
            var clusterCached0 = new HashSet<GrainId>();
            var clusterCached1 = new HashSet<GrainId>();
            var clusterOwned0 = new HashSet<GrainId>();
            var clusterOwned1 = new HashSet<GrainId>();

            clusterCached0.UnionWith(GetGrainsInClusterWithStatus(cluster0, MultiClusterStatus.Cached));
            clusterOwned0.UnionWith(GetGrainsInClusterWithStatus(cluster0, MultiClusterStatus.Owned));
            clusterCached1.UnionWith(GetGrainsInClusterWithStatus(cluster1, MultiClusterStatus.Cached));
            clusterOwned1.UnionWith(GetGrainsInClusterWithStatus(cluster1, MultiClusterStatus.Owned));
           
            // Since both clients raced to create the same grain, 
            // we expect one cluster to contain a CACHED activation of the grain, 
            // and the other to contain an OWNED activation of the grain. 

            var compare_0_1 = new HashSet<GrainId>(clusterCached0);
            var compare_1_0 = new HashSet<GrainId>(clusterCached1);
            foreach (GrainId g in clusterOwned1)
            {
                if (compare_0_1.Contains(g))
                    compare_0_1.Remove(g);
                else
                    compare_0_1.Add(g);
            }
            foreach (GrainId g in clusterOwned0)
            {
                if (compare_1_0.Contains(g))
                    compare_1_0.Remove(g);
                else
                    compare_1_0.Add(g);
            }
            Assert.AreEqual(clusterCached0.Count, clusterOwned1.Count - clusterBase1,
                "Cached-0 vs Owned-1 -- Variance = " + Utils.EnumerableToString(compare_0_1));
            Assert.AreEqual(clusterCached1.Count, clusterOwned0.Count- clusterBase0,
                "Cached-1 vs Owned-0 -- Variance = " + Utils.EnumerableToString(compare_1_0));

            // Double check that cached and owned lists match across clusters
            foreach (var grain in clusterCached0)
            {
                Assert.IsTrue(clusterOwned1.Contains(grain));
            }
            foreach (var grain in clusterCached1)
            {
                Assert.IsTrue(clusterOwned0.Contains(grain));
            }
        }

        private List<GrainId> GetGrainsInClusterWithStatus(string clusterId, MultiClusterStatus? status = null)
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

        private Dictionary<GrainId,List<IActivationInfo>> GetGrainActivations()
        {
            var grains = new Dictionary<GrainId, List<IActivationInfo>>();
            
            int graincount = 0;
            int instancecount = 0;

            foreach(var kvp in Clusters)
                foreach (var silo in kvp.Value.Silos)
                {
                    var dir = silo.Silo.TestHook.GetDirectoryForTypeNamesContaining("ClusterTestGrain");

                    foreach (var grainKeyValue in dir)
                    {
                        GrainId grainId = grainKeyValue.Key;
                        IGrainInfo grainInfo = grainKeyValue.Value;

                        graincount++;
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

            WriteLog("Returning: {0} instances for {1} grains", instancecount, graincount);

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
                    int ret = threadArg.client.CallGrain(grainId);

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

        private struct ClientIdentity
        {
            public string cluster;
            public int number;
        }

        // This function takes two arguments, a list of client configurations, and an integer. The list of client configurations is used to
        // create multiple clients that concurrently call the grains in range [0, numGrains). We run the experiment in a series of barriers.
        // The clients all invoke grain "g", in parallel, and then wait on a signal by the main thread (this function). The main thread, then 
        // wakes up the clients, after which they invoke "g+1", and so on.
        private List<Tuple<int, int>>[] DoConcurrentExperiment(List<ClientIdentity> configList, int numGrains)
        {
            WriteLog("Starting concurrent experiment");

            // We use two objects to coordinate client threads and the main thread. coordWakeup is an object that is used to signal the coordinator
            // thread. toWait is used to signal client threads.
            var coordWakeup = new object();
            var toWait = new object();

            // We keep a list of client threads.
            var clientThreads = new List<Tuple<Thread, ClientThreadArgs>>();
            var rand = new Random();
            var results = new List<Tuple<int, int>>[configList.Count];
            threadsDone = results.Length;

            int index = 0;

            // Create a client thread corresponding to each configuration file.
            // The client thread will execute ThreadFunc function.
            foreach (var clientidentity in configList)
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
                var client = NewClient<ClientWrapper>(clientidentity.cluster, clientidentity.number);
                var thread = new Thread(ThreadFunc);
                var threadFuncArgs = new ClientThreadArgs
                {
                    client = client,
                    args = args,
                    resultIndex = index,
                    numThreads = configList.Count,
                    coordWakeup = coordWakeup,
                    toWait = toWait,
                    results = results,
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
                WaitForWorkers(configList.Count, coordWakeup, toWait);
            }

            // Once the clients threads have finished calling the grain the appropriate number of times, we wait for them to write out their results.
            foreach (var threadNArg in clientThreads)
            {
                var thread = threadNArg.Item1;
                thread.Join();
            }

            // Finally, we return an array of results.
            return results;
        }


   
    }
    #endregion
}
