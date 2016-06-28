using Orleans;
using Orleans.Runtime;
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
    public class MultiClusterRegistrationTests : TestingClusterHost, IDisposable
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

            // Create 3 clusters, each with 2 silos. 
            NewGeoCluster(globalserviceid, ClusterA, 2);
            NewGeoCluster(globalserviceid, ClusterB, 2);
            NewGeoCluster(globalserviceid, ClusterC, 2);

            WriteLog("Clusters are ready (elapsed = {0})", stopwatch.Elapsed);

            WaitForLivenessToStabilizeAsync().WaitWithThrow(TimeSpan.FromMinutes(1));

            // Create two clients per cluster.
            ClientA0 = NewClient<ClientWrapper>(ClusterA, 0);
            ClientA1 = NewClient<ClientWrapper>(ClusterA, 1);
            ClientB0 = NewClient<ClientWrapper>(ClusterB, 0);
            ClientB1 = NewClient<ClientWrapper>(ClusterB, 1);
            ClientC0 = NewClient<ClientWrapper>(ClusterC, 0);
            ClientC1 = NewClient<ClientWrapper>(ClusterC, 1);

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


        [Fact, TestCategory("GeoCluster")]
        public async Task All()
        {
            var testtasks = new List<Task>();

            testtasks.Add(RunTest("Deact", Deact, 20));
            testtasks.Add(RunTest("LocalRegistration", LocalRegistration));
            testtasks.Add(RunTest("SequentialCalls", SequentialCalls));
            testtasks.Add(RunTest("ParallelCalls", ParallelCalls));
            testtasks.Add(RunTest("ManyParallelCalls", ManyParallelCalls));

            await Task.WhenAll(testtasks);
        }


        private Task RunTest(string name, Func<Task> test, int timeout = 10)
        {
            return RunWithTimeout(name, timeout * 1000, test);
        }
     

        public async Task SequentialCalls()
        {
            await Task.Yield();

            var x = Next();
            ClientA0.CallGrain(x);
            ClientA1.CallGrain(x);
            ClientB0.CallGrain(x);
            ClientB1.CallGrain(x);
            ClientC0.CallGrain(x);
            ClientC1.CallGrain(x);
            Assert.Equal(7, ClientA0.CallGrain(x));
        }

        public async Task ParallelCalls()
        {
            await Task.Yield();

            var x = Next();
            Parallel.Invoke(
              () => ClientA0.CallGrain(x),
              () => ClientA1.CallGrain(x),
              () => ClientB0.CallGrain(x),
              () => ClientB1.CallGrain(x),
              () => ClientC0.CallGrain(x),
              () => ClientC1.CallGrain(x)
            );
            Assert.Equal(7, ClientA0.CallGrain(x));
        }

        public async Task ManyParallelCalls()
        {
            await Task.Yield();

            var x = Next();
            var clients = new ClientWrapper[] { ClientA0, ClientB0, ClientC0 };
            // concurrently chaotically increment (numupdates) times
            Parallel.For(0, 20, new ParallelOptions() { MaxDegreeOfParallelism = 4 }, i => clients[i % 3].CallGrain(x));
            Assert.Equal(21, ClientC1.CallGrain(x));
        }

        private class GrainInfo
        {
            public int x;
            public string runtimeid;
            public int client;
        }

        public async Task LocalRegistration()
        {
            await Task.Yield();

            Dictionary<string, List<GrainInfo>> grainsbysilo = new Dictionary<string, List<GrainInfo>>();

            // each client allocates 20 grains, all of which are in their respective cluster
            Parallel.For<List<GrainInfo>>(0, 120, new ParallelOptions() { MaxDegreeOfParallelism = 4 },
                 () => new List<GrainInfo>(),
                 (i, s, list) =>
                 {
                     var g = new GrainInfo();
                     g.x = Next();
                     g.client = i % Clients.Length;
                     g.runtimeid = Clients[g.client].GetRuntimeId(g.x);
                     var count = Clients[g.client].CallGrain(g.x);
                     Assert.Equal(1, count);
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
            Assert.Equal(6, grainsbysilo.Keys.Count);

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
                    Assert.Equal(g.client / 3, i / 3);

            // deactivate the grains
            Parallel.ForEach(grainsbysilo.SelectMany((kvp, i) => kvp.Value),
                new ParallelOptions() { MaxDegreeOfParallelism = 4 },
                g => { Clients[g.client].Deactivate(g.x); });

            // wait 5 seconds for deactivations
            await Task.Delay(5000);

            // permute grain identifiers
            var ids = GrainsBySiloSorted.SelectMany((list, i) => list).ToList();
            ids.Sort((a, b) => a.x.CompareTo(b.x));

            // reactivate and check that we are fresh, and in local cluster
            Parallel.ForEach(GrainsBySiloSorted.SelectMany((list, i) => list),
               new ParallelOptions() { MaxDegreeOfParallelism = 4 },
               g =>
               {
                   // since grain was deactivated, count should be 1
                   var count = Clients[Next() % Clients.Length].CallGrain(g.x);
                   Assert.Equal(1, count);
               });

        }
       
        public async Task Deact()
        {
            var x = Next();
            var id = ClientA0.GetRuntimeId(x);
            WriteLog("Grain {0} at {1}", ClientA0.GetGrainRef(x), id);
            Assert.True(id == Clusters[ClusterA].Silos[0].Silo.SiloAddress.ToString()
                     || id == Clusters[ClusterA].Silos[1].Silo.SiloAddress.ToString());

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
            Assert.Equal(1, val);
            var newid = ClientB0.GetRuntimeId(x);
            WriteLog("Grain {0} at {1}", ClientB0.GetGrainRef(x), newid);
            Assert.True(newid == Clusters[ClusterB].Silos[0].Silo.SiloAddress.ToString()
                     || newid == Clusters[ClusterB].Silos[1].Silo.SiloAddress.ToString());
        }
    }
}
