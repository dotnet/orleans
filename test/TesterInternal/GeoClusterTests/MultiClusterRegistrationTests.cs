using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TestGrainInterfaces;
using Tests.GeoClusterTests;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.GeoClusterTests
{
    public class MultiClusterRegistrationTests : TestingClusterHost
    {
        private const string ClusterA = "A";
        private const string ClusterB = "B";
        private const string ClusterC = "C";

        private static ClientWrapper ClientA0;
        private static ClientWrapper ClientA1;
        private static ClientWrapper ClientB0;
        private static ClientWrapper ClientB1;
        private static ClientWrapper ClientC0;
        private static ClientWrapper ClientC1;

        #region client wrappers
        public class ClientWrapper : ClientWrapperBase
        {
            public ClientWrapper(string name, int gatewayport) : base(name, gatewayport)
            {
                systemManagement = GrainClient.GrainFactory.GetGrain<IManagementGrain>(0);
            }
            public int CallGrain(int i)
            {
                var grainRef = GrainClient.GrainFactory.GetGrain<IClusterTestGrain>(i);
                GrainClient.Logger.Info("Call Grain {0}", grainRef);
                Task<int> toWait = grainRef.SayHelloAsync();
                toWait.Wait();
                return toWait.Result;
            }
            public string GetRuntimeId(int i)
            {
                var grainRef = GrainClient.GrainFactory.GetGrain<IClusterTestGrain>(i);
                GrainClient.Logger.Info("GetRuntimeId {0}", grainRef);
                Task<string> toWait = grainRef.GetRuntimeId();
                toWait.Wait();
                return toWait.Result;
            }
            public void Deactivate(int i)
            {
                var grainRef = GrainClient.GrainFactory.GetGrain<IClusterTestGrain>(i);
                GrainClient.Logger.Info("Deactivate {0}", grainRef);
                Task toWait = grainRef.Deactivate();
                toWait.Wait();
            }
            public void InjectClusterConfiguration(params string[] clusters)
            {
                systemManagement.InjectMultiClusterConfiguration(clusters).Wait();
            }
            IManagementGrain systemManagement;
            public string GetGrainRef(int i)
            {
                return GrainClient.GrainFactory.GetGrain<IClusterTestGrain>(i).ToString();
            }
        }
        #endregion


        public MultiClusterRegistrationTests(ITestOutputHelper output) : base(output)
        {
            var inittask = Task.Run(() => StartClustersAndClients());
            // setting up clusters can take long legitimately - but it can also hang forever sometimes
            // so we need to set a timeout
            inittask.WaitWithThrow(TimeSpan.FromMinutes(System.Diagnostics.Debugger.IsAttached ? 60 : 5));
        }

        public void StartClustersAndClients() 
        {
            WriteLog("Creating clusters and clients...");
            var stopwatch = new System.Diagnostics.Stopwatch();
            stopwatch.Start();

            // use a random global service id for testing purposes
            var globalserviceid = Guid.NewGuid();
            random = new Random();

            System.Threading.ThreadPool.SetMaxThreads(8, 8);

            Action<ClusterConfiguration> addtracing = (ClusterConfiguration c) =>
            {
                // logging  
                foreach (var o in c.Overrides)
                {
                    o.Value.TraceLevelOverrides.Add(new Tuple<string, Severity>("Runtime.Catalog", Severity.Verbose));
                    o.Value.TraceLevelOverrides.Add(new Tuple<string, Severity>("Orleans.GrainDirectory.LocalGrainDirectory", Severity.Verbose));
                }
            };

            // Create 3 clusters, each with 2 silos. 
            NewGeoCluster(globalserviceid, ClusterA, 2, addtracing);
            NewGeoCluster(globalserviceid, ClusterB, 2, addtracing);
            NewGeoCluster(globalserviceid, ClusterC, 2, addtracing);

            WriteLog("Clusters are ready (elapsed = {0})", stopwatch.Elapsed);

            WaitForLivenessToStabilizeAsync().WaitWithThrow(TimeSpan.FromMinutes(1));

            // Create two clients per cluster.
            Parallel.Invoke(paralleloptions,
              () => ClientA0 = NewClient<ClientWrapper>(ClusterA, 0),
              () => ClientA1 = NewClient<ClientWrapper>(ClusterA, 1),
              () => ClientB0 = NewClient<ClientWrapper>(ClusterB, 0),
              () => ClientB1 = NewClient<ClientWrapper>(ClusterB, 1),
              () => ClientC0 = NewClient<ClientWrapper>(ClusterC, 0),
              () => ClientC1 = NewClient<ClientWrapper>(ClusterC, 1)
            );

            WriteLog("Clients are ready (elapsed = {0})", stopwatch.Elapsed);

            ClientA0.InjectClusterConfiguration(ClusterA, ClusterB, ClusterC);
            WaitForMultiClusterGossipToStabilizeAsync(false).WaitWithThrow(TimeSpan.FromMinutes(System.Diagnostics.Debugger.IsAttached ? 60 : 1));

            Clients = new ClientWrapper[] { ClientA0, ClientA1, ClientB0, ClientB1, ClientC0, ClientC1 };
            ClientClusters = new string[] { ClusterA, ClusterA, ClusterB, ClusterB, ClusterC, ClusterC };

            stopwatch.Stop();
            WriteLog("Clusters and clients are ready (elapsed = {0}).", stopwatch.Elapsed);
        }

        private ClientWrapper[] Clients;
        private string[] ClientClusters;


        Random random;
        private int Next()
        {
            lock (random)
                return random.Next();
        }

        [Fact, TestCategory("GeoCluster")]
        public async Task All()
        {
            var testtasks = new List<Task>();

            testtasks.Add(RunWithTimeout("Deact", 20000, Deact));
            testtasks.Add(RunWithTimeout("LocalRegistration", 10000, LocalRegistration));
            testtasks.Add(RunWithTimeout("SequentialCalls", 10000, SequentialCalls));
            testtasks.Add(RunWithTimeout("ParallelCalls", 10000, ParallelCalls));
            testtasks.Add(RunWithTimeout("ManyParallelCalls", 10000, ManyParallelCalls));

            foreach (var t in testtasks)
                await t;
        }

        public async Task SequentialCalls()
        {
            await Task.Yield();

            var x = Next();
            var gref = ClientA0.GetGrainRef(x);
            ClientA0.CallGrain(x);
            ClientA1.CallGrain(x);
            ClientB0.CallGrain(x);
            ClientB1.CallGrain(x);
            ClientC0.CallGrain(x);
            ClientC1.CallGrain(x);
            AssertEqual(7, ClientA0.CallGrain(x), gref);
        }

        public async Task ParallelCalls()
        {
            await Task.Yield();

            var x = Next();
            var gref = ClientA0.GetGrainRef(x);
            Parallel.Invoke(paralleloptions,
              () => ClientA0.CallGrain(x),
              () => ClientA1.CallGrain(x),
              () => ClientB0.CallGrain(x),
              () => ClientB1.CallGrain(x),
              () => ClientC0.CallGrain(x),
              () => ClientC1.CallGrain(x)
            );
            AssertEqual(7, ClientA0.CallGrain(x), gref);
        }

        public async Task ManyParallelCalls()
        {
            await Task.Yield();

            var x = Next();
            var gref = ClientA0.GetGrainRef(x);
            var clients = new ClientWrapper[] { ClientA0, ClientB0, ClientC0 };
            // concurrently chaotically increment (numupdates) times
            Parallel.For(0, 20, paralleloptions, i => clients[i % 3].CallGrain(x));
            AssertEqual(21, ClientC1.CallGrain(x), gref);
        }

        private class GrainInfo
        {
            public int x;
            public string gref;
            public string runtimeid;
            public int client;
        }

        public async Task LocalRegistration()
        {
            await Task.Yield();

            Dictionary<string, List<GrainInfo>> grainsbysilo = new Dictionary<string, List<GrainInfo>>();

            // each client allocates 20 grains, all of which are in their respective cluster
            Parallel.For<List<GrainInfo>>(0, 120, paralleloptions,
                 () => new List<GrainInfo>(),
                 (i, s, list) =>
                 {
                     var g = new GrainInfo();
                     g.x = Next();
                     g.client = i % Clients.Length;
                     var client = Clients[g.client];
                     g.gref = client.GetGrainRef(g.x);
                     var count = client.CallGrain(g.x);
                     AssertEqual(1, count, g.gref);
                     g.runtimeid = client.GetRuntimeId(g.x);
                     Assert.NotNull(g.runtimeid);
                     list.Add(g);
                     return list;
                 },
                 (list) =>
                 {
                     foreach (var g in list)
                     {
                         List<GrainInfo> silolist;
                         if (!grainsbysilo.TryGetValue(g.runtimeid, out silolist))
                             grainsbysilo.Add(g.runtimeid, silolist = new List<GrainInfo>());
                         silolist.Add(g);
                     }
                 });

            // there should be 6 different silos
            AssertEqual(6, grainsbysilo.Keys.Count, "all silos get some grains");

            foreach (var kvp in grainsbysilo)
            {
                WriteLog("silo {0} in cluster {1} has {2} grains", kvp.Key, ClientClusters[kvp.Value[0].client], kvp.Value.Count);
            }

            // store by client that is connected to the silo
            var GrainsBySiloSorted = new List<GrainInfo>[] {
             grainsbysilo[Clusters[ClusterA].Silos[0].Silo.SiloAddress.ToString()],
             grainsbysilo[Clusters[ClusterA].Silos[1].Silo.SiloAddress.ToString()],
             grainsbysilo[Clusters[ClusterB].Silos[0].Silo.SiloAddress.ToString()],
             grainsbysilo[Clusters[ClusterB].Silos[1].Silo.SiloAddress.ToString()],
             grainsbysilo[Clusters[ClusterC].Silos[0].Silo.SiloAddress.ToString()],
             grainsbysilo[Clusters[ClusterC].Silos[1].Silo.SiloAddress.ToString()]
            };


            // clients should have grains that are local to their cluster
            for (int i = 0; i < 6; i++)
                foreach (var g in GrainsBySiloSorted[i])
                    AssertEqual(g.client / 2, i / 2, g.gref);

            // deactivate the grains
            Parallel.ForEach(grainsbysilo.SelectMany((kvp, i) => kvp.Value),
                paralleloptions,
                g => { Clients[g.client].Deactivate(g.x); });

            // wait 5 seconds for deactivations
            await Task.Delay(5000);

            // permute grain identifiers
            var ids = GrainsBySiloSorted.SelectMany((list, i) => list).ToList();
            ids.Sort((a, b) => a.x.CompareTo(b.x));

            // reactivate and check that we are fresh, and in local cluster
            Parallel.ForEach(GrainsBySiloSorted.SelectMany((list, i) => list),
               paralleloptions,
               g =>
               {
                   // since grain was deactivated, count should be 1
                   var count = Clients[Next() % Clients.Length].CallGrain(g.x);
                   AssertEqual(1, count, g.gref);
               });

        }
       
        public async Task Deact()
        {
            var x = Next();
            var gref = ClientA0.GetGrainRef(x);
            var id = ClientA0.GetRuntimeId(x);
            WriteLog("Grain {0} at {1}", ClientA0.GetGrainRef(x), id);
            Assert.True(id == Clusters[ClusterA].Silos[0].Silo.SiloAddress.ToString()
                     || id == Clusters[ClusterA].Silos[1].Silo.SiloAddress.ToString(), gref);

            // ensure presence in caches
            ClientA0.CallGrain(x);  
            ClientA1.CallGrain(x);  
            ClientB0.CallGrain(x);  
            ClientB1.CallGrain(x);

            //deactivate
            ClientA0.Deactivate(x);

            // wait for deactivation to complete
            await Task.Delay(5000);

            // should be gone now, and allocated in same cluster as client
            var val = ClientB0.CallGrain(x);
            AssertEqual(1, val, gref);
            var newid = ClientB0.GetRuntimeId(x);
            WriteLog("Grain {0} at {1}", ClientB0.GetGrainRef(x), newid);
            Assert.True(newid == Clusters[ClusterB].Silos[0].Silo.SiloAddress.ToString()
                     || newid == Clusters[ClusterB].Silos[1].Silo.SiloAddress.ToString(), gref);
        }
    }
}
