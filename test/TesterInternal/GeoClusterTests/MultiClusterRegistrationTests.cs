#if !NETCOREAPP
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.TestingHost;
using TestGrainInterfaces;
using Tests.GeoClusterTests;
using Xunit;
using Xunit.Abstractions;
using Orleans.Hosting;
using Orleans.Internal;

namespace UnitTests.GeoClusterTests
{
    [TestCategory("GeoCluster")]
    public class MultiClusterRegistrationTests : TestingClusterHost
    {
        private string[] ClusterNames;
        private ClientWrapper[][] Clients;

        private IEnumerable<KeyValuePair<string, ClientWrapper>> EnumerateClients()
        {
            for (int i = 0; i < ClusterNames.Length; i++)
                foreach (var c in Clients[i])
                    yield return new KeyValuePair<string, ClientWrapper>(ClusterNames[i], c);
        }

        public MultiClusterRegistrationTests(ITestOutputHelper output) : base(output)
        {
        }

        [SkippableFact]
        public async Task TwoClusterBattery()
        {

            await RunWithTimeout("Start Clusters and Clients", 180 * 1000, () => StartClustersAndClients(2, 2));

            var testtasks = new List<Task>();

            testtasks.Add(RunWithTimeout("Deact", 20000, Deact));
            testtasks.Add(RunWithTimeout("SequentialCalls", 10000, SequentialCalls));
            testtasks.Add(RunWithTimeout("ParallelCalls", 10000, ParallelCalls));
            testtasks.Add(RunWithTimeout("ManyParallelCalls", 10000, ManyParallelCalls));
            testtasks.Add(RunWithTimeout("ObserverBasedClientNotification", 10000, ObserverBasedClientNotification));
            testtasks.Add(RunWithTimeout("StreamBasedClientNotification", 10000, StreamBasedClientNotification));

            foreach (var t in testtasks)
                await t;
        }

        [SkippableFact(), TestCategory("Functional")]
        public async Task ThreeClusterBattery()
        {

            await RunWithTimeout("Start Clusters and Clients", 180 * 1000, () => StartClustersAndClients(2, 2, 2));

            var testtasks = new List<Task>();

            testtasks.Add(RunWithTimeout("Deact", 20000, Deact));
            testtasks.Add(RunWithTimeout("SequentialCalls", 10000, SequentialCalls));
            testtasks.Add(RunWithTimeout("ParallelCalls", 10000, ParallelCalls));
            testtasks.Add(RunWithTimeout("ManyParallelCalls", 10000, ManyParallelCalls));
            testtasks.Add(RunWithTimeout("ObserverBasedClientNotification", 10000, ObserverBasedClientNotification));
            testtasks.Add(RunWithTimeout("StreamBasedClientNotification", 10000, StreamBasedClientNotification));

            foreach (var t in testtasks)
                await t;
        }

        [SkippableFact]
        public async Task FourClusterBattery()
        {

            await RunWithTimeout("Start Clusters and Clients", 180 * 1000, () => StartClustersAndClients(2, 2, 1, 1));

            var testtasks = new List<Task>();

            for (int i = 0; i < 20; i++)
            {
                testtasks.Add(RunWithTimeout("Deact", 20000, Deact));
                testtasks.Add(RunWithTimeout("SequentialCalls", 10000, SequentialCalls));
                testtasks.Add(RunWithTimeout("ParallelCalls", 10000, ParallelCalls));
                testtasks.Add(RunWithTimeout("ManyParallelCalls", 10000, ManyParallelCalls));
                testtasks.Add(RunWithTimeout("ObserverBasedClientNotification", 10000, ObserverBasedClientNotification));
                testtasks.Add(RunWithTimeout("StreamBasedClientNotification", 10000, StreamBasedClientNotification));
            }

            foreach (var t in testtasks)
                await t;
        }

        private Task StartClustersAndClients(params short[] silos)
        {
            return StartClustersAndClients(null, silos);
        }

        public class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.AddSimpleMessageStreamProvider("SMSProvider");
            }
        }

        private Task StartClustersAndClients(Action<TestClusterBuilder> configureTestCluster, params short[] silos)
        {
            WriteLog("Creating clusters and clients...");
            var stopwatch = new System.Diagnostics.Stopwatch();
            stopwatch.Start();

            // use a random global service id for testing purposes
            var globalserviceid = Guid.NewGuid();
            random = new Random();

            System.Threading.ThreadPool.SetMaxThreads(8, 8);

            // Create clusters and clients
            ClusterNames = new string[silos.Length];
            Clients = new ClientWrapper[silos.Length][];
            for (int i = 0; i < silos.Length; i++)
            {
                var numsilos = silos[i];
                var clustername = ClusterNames[i] = ((char)('A' + i)).ToString();
                var c = Clients[i] = new ClientWrapper[numsilos];
                NewGeoCluster<SiloConfigurator>(globalserviceid, clustername, silos[i], configureTestCluster);
                // create one client per silo
                Parallel.For(0, numsilos, paralleloptions, (j) => c[j] = this.NewClient(
                    clustername,
                    j,
                    ClientWrapper.Factory,
                    clientBuilder => clientBuilder.AddSimpleMessageStreamProvider("SMSProvider")));
            }

            WriteLog("Clusters and clients are ready (elapsed = {0})", stopwatch.Elapsed);

            // wait for configuration to stabilize
            WaitForLivenessToStabilizeAsync().WaitWithThrow(TimeSpan.FromMinutes(1));

            Clients[0][0].InjectClusterConfiguration(ClusterNames);
            WaitForMultiClusterGossipToStabilizeAsync(false).WaitWithThrow(TimeSpan.FromMinutes(System.Diagnostics.Debugger.IsAttached ? 60 : 1));

            stopwatch.Stop();
            WriteLog("Multicluster is ready (elapsed = {0}).", stopwatch.Elapsed);

            return Task.CompletedTask;
        }


        Random random;

        public class ClientWrapper : ClientWrapperBase
        {
            public static readonly Func<string, int, string, Action<IClientBuilder>, ClientWrapper> Factory =
                (name, gwPort, clusterId, clientConfigurator) => new ClientWrapper(name, gwPort, clusterId, clientConfigurator);
            
            public ClientWrapper(string name, int gatewayport, string clusterId, Action<IClientBuilder> clientConfigurator) : base(name, gatewayport, clusterId, clientConfigurator)
            {
                this.systemManagement = this.GrainFactory.GetGrain<IManagementGrain>(0);
            }
            public int CallGrain(int i)
            {
                var grainRef = this.GrainFactory.GetGrain<IClusterTestGrain>(i);
                this.Logger.Info("Call Grain {0}", grainRef);
                Task<int> toWait = grainRef.SayHelloAsync();
                toWait.Wait();
                return toWait.GetResult();
            }
            public string GetRuntimeId(int i)
            {
                var grainRef = this.GrainFactory.GetGrain<IClusterTestGrain>(i);
                this.Logger.Info("GetRuntimeId {0}", grainRef);
                Task<string> toWait = grainRef.GetRuntimeId();
                toWait.Wait();
                return toWait.GetResult();
            }
            public void Deactivate(int i)
            {
                var grainRef = this.GrainFactory.GetGrain<IClusterTestGrain>(i);
                this.Logger.Info("Deactivate {0}", grainRef);
                Task toWait = grainRef.Deactivate();
                toWait.GetResult();
            }

            public void EnableStreamNotifications(int i)
            {
                var grainRef = this.GrainFactory.GetGrain<IClusterTestGrain>(i);
                this.Logger.Info("EnableStreamNotifications {0}", grainRef);
                Task toWait = grainRef.EnableStreamNotifications();
                toWait.GetResult();
            }

            // observer-based notification
            public void Subscribe(int i, IClusterTestListener listener)
            {
                var grainRef = this.GrainFactory.GetGrain<IClusterTestGrain>(i);
                this.Logger.Info("Create Listener object {0}", grainRef);
                listeners.Add(listener);
                var obj = this.GrainFactory.CreateObjectReference<IClusterTestListener>(listener).Result;
                listeners.Add(obj);
                this.Logger.Info("Subscribe {0}", grainRef);
                Task toWait = grainRef.Subscribe(obj);
                toWait.GetResult();
            }
            List<IClusterTestListener> listeners = new List<IClusterTestListener>(); // keep them from being GCed

            // stream-based notification
            public void SubscribeStream(int i, IAsyncObserver<int> listener)
            {
                IStreamProvider streamProvider = this.Client.GetStreamProvider("SMSProvider");
                Guid guid = new Guid(i, 0, 0, new byte[8]);
                IAsyncStream<int> stream = streamProvider.GetStream<int>(guid, "notificationtest");
                handle = stream.SubscribeAsync(listener).GetResult();
            }
            StreamSubscriptionHandle<int> handle;

            public void InjectClusterConfiguration(string[] clusters, string comment = "", bool checkForLaggingSilos = true)
            {
                systemManagement.InjectMultiClusterConfiguration(clusters, comment, checkForLaggingSilos).Wait();
            }
            IManagementGrain systemManagement;
            public string GetGrainRef(int i)
            {
                return this.GrainFactory.GetGrain<IClusterTestGrain>(i).ToString();
            }
        }

        public class ClusterTestListener : IClusterTestListener, IAsyncObserver<int>
        {
            public ClusterTestListener(Action<int> oncall)
            {
                this.oncall = oncall;
            }

            private Action<int> oncall;

            public void GotHello(int number)
            {
                count++;
                oncall(number);
            }

            public Task OnNextAsync(int item, StreamSequenceToken token = null)
            {
                GotHello(item);
                return Task.CompletedTask;
            }

            public Task OnCompletedAsync()
            {
                throw new NotImplementedException();
            }

            public Task OnErrorAsync(Exception ex)
            {
                throw new NotImplementedException();
            }

            public int count;
        }


        private int Next()
        {
            lock (random)
                return random.Next();
        }
        private int[] Next(int count)
        {
            var result = new int[count];
            lock (random)
                for (int i = 0; i < count; i++)
                    result[i] = random.Next();
            return result;
        }

        private async Task SequentialCalls()
        {
            await Task.Yield();

            var x = Next();
            var gref = Clients[0][0].GetGrainRef(x);
            var list = EnumerateClients().ToList();

            // one call to each silo, in parallel
            foreach (var c in list) c.Value.CallGrain(x);
            
            // total number of increments should match
            AssertEqual(list.Count() + 1, Clients[0][0].CallGrain(x), gref);
        }

        private async Task ParallelCalls()
        {
            await Task.Yield();

            var x = Next();
            var gref = Clients[0][0].GetGrainRef(x);
            var list = EnumerateClients().ToList();

            // one call to each silo, in parallel
            Parallel.ForEach(list, paralleloptions, k => k.Value.CallGrain(x));

            // total number of increments should match
            AssertEqual(list.Count + 1, Clients[0][0].CallGrain(x), gref);
        }

        private async Task ManyParallelCalls()
        {
            await Task.Yield();

            var x = Next();
            var gref = Clients[0][0].GetGrainRef(x);

            // pick just one client per cluster, use it multiple times
            var clients = Clients.Select(a => a[0]).ToList();

            // concurrently increment (numupdates) times, distributed over the clients
            Parallel.For(0, 20, paralleloptions, i => clients[i % clients.Count].CallGrain(x));

            // total number of increments should match
            AssertEqual(21, Clients[0][0].CallGrain(x), gref);
        }

        private async Task Deact()
        {            
            var x = Next();
            var gref = Clients[0][0].GetGrainRef(x);

            // put into cluster A
            var id = Clients[0][0].GetRuntimeId(x);

            WriteLog("Grain {0} at {1}", gref, id);
            Assert.Contains(Clusters[ClusterNames[0]].Silos, silo => silo.SiloAddress.ToString() == id);

            // ensure presence in all caches
            var list = EnumerateClients().ToList();
            Parallel.ForEach(list, paralleloptions, k => k.Value.CallGrain(x));
            Parallel.ForEach(list, paralleloptions, k => k.Value.CallGrain(x));

            AssertEqual(2* list.Count() + 1, Clients[0][0].CallGrain(x), gref);

            WriteLog("Grain {0} deactivating.", gref);

            //deactivate
            Clients[0][0].Deactivate(x);

            // wait for deactivation to complete
            await Task.Delay(5000);

            WriteLog("Grain {0} reactivating.", gref);

            // activate anew in cluster B
            var val = Clients[1][0].CallGrain(x);
            AssertEqual(1, val, gref);
            var newid = Clients[1][0].GetRuntimeId(x);
            WriteLog("{2} sees Grain {0} at {1}", gref, newid, ClusterNames[1]);
            Assert.Contains(Clusters[ClusterNames[1]].Silos, silo => silo.SiloAddress.ToString() == newid);

            WriteLog("Grain {0} Check that other clusters find new activation.", gref);
            for (int i = 2; i < Clusters.Count; i++)
            {
                var idd = Clients[i][0].GetRuntimeId(x);
                WriteLog("{2} sees Grain {0} at {1}", gref, idd, ClusterNames[i]);
                AssertEqual(newid, idd, gref);
            }
        }


        private async Task ObserverBasedClientNotification()
        {
            var x = Next();
            var gref = Clients[0][0].GetGrainRef(x);
            Clients[0][0].GetRuntimeId(x);
            WriteLog("{0} created grain", gref);

            var promises = new List<Task<int>>();

            // create an observer on each client
            Parallel.For(0, Clients.Length, paralleloptions, i =>
            {
                for (int jj = 0; jj < Clients[i].Length; jj++)
                {
                    int j = jj;
                    var promise = new TaskCompletionSource<int>();
                    var listener = new ClusterTestListener((num) =>
                    {
                        WriteLog("{3} observedcall {2} on Client[{0}][{1}]", i, j, num, gref);
                        promise.TrySetResult(num);
                    });
                    lock(promises)
                        promises.Add(promise.Task);
                    Clients[i][j].Subscribe(x, listener);
                    WriteLog("{2} subscribed to Client[{0}][{1}]", i, j, gref);
                }
            });

            // call the grain
            Clients[0][0].CallGrain(x);

            await Task.WhenAll(promises);

            var sortedresults = promises.Select(p => p.Result).OrderBy(num => num).ToArray();

            // each client should get its own notification
            for (int i = 0; i < sortedresults.Length; i++)
                AssertEqual(sortedresults[i], i, gref);
        }

        private async Task StreamBasedClientNotification()
        {
            var x = Next();
            var gref = Clients[0][0].GetGrainRef(x);
            Clients[0][0].EnableStreamNotifications(x);
            WriteLog("{0} created grain", gref);

            var promises = new List<Task<int>>();

            // create an observer on each client
            Parallel.For(0, Clients.Length, paralleloptions, i =>
            {
                for (int jj = 0; jj < Clients[i].Length; jj++)
                {
                    int j = jj;
                    var promise = new TaskCompletionSource<int>();
                    var listener = new ClusterTestListener((num) =>
                    {
                        WriteLog("{3} observedcall {2} on Client[{0}][{1}]", i, j, num, gref);
                        promise.TrySetResult(num);
                    });
                    lock (promises)
                        promises.Add(promise.Task);
                    Clients[i][j].SubscribeStream(x, listener);
                    WriteLog("{2} subscribed to Client[{0}][{1}]", i, j, gref);
                }
            });
            // call the grain
            Clients[0][0].CallGrain(x);

            await Task.WhenAll(promises);

            // each client should get same value
            foreach (var p in promises)
                AssertEqual(1, p.Result, gref);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task BlockedDeact()
        {
            await RunWithTimeout("Start Clusters and Clients", 180 * 1000, () =>
            {
                return StartClustersAndClients(2, 2);
            });

            await RunWithTimeout("BlockedDeact", 10 * 1000, async () =>
            {
                var x = Next();
                var gref = Clients[0][0].GetGrainRef(x);

                // put into cluster A and access from cluster B
                var id = Clients[0][0].GetRuntimeId(x);

                WriteLog("Grain {0} at {1}", gref, id);
                Assert.Contains(Clusters[ClusterNames[0]].Silos, silo => silo.SiloAddress.ToString() == id);

                var id2 = Clients[1][0].GetRuntimeId(x);
                AssertEqual(id2, id, gref);

                // deactivate grain in cluster A, but block deactivation message to cluster B

                WriteLog("Grain {0} deactivating.", gref);
                BlockAllClusterCommunication(ClusterNames[0], ClusterNames[1]);
                Clients[0][0].Deactivate(x);
                await Task.Delay(5000);
                UnblockAllClusterCommunication(ClusterNames[0]);

                // reactivate in cluster B. This should cause unregistration to be sent
                WriteLog("Grain {0} reactivating.", gref);

                // activate anew in cluster B
                var val = Clients[1][0].CallGrain(x);
                AssertEqual(1, val, gref);
                var newid = Clients[1][0].GetRuntimeId(x);
                WriteLog("{2} sees Grain {0} at {1}", gref, newid, ClusterNames[1]);
                Assert.Contains(newid, Clusters[ClusterNames[1]].Silos.Select(s => s.SiloAddress.ToString()));

                // connect from cluster A
                val = Clients[0][0].CallGrain(x);
                AssertEqual(2, val, gref);
            });
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task CacheCleanup()
        {
            await RunWithTimeout("Start Clusters and Clients", 180 * 1000, () =>
            {
                return StartClustersAndClients(1, 1);
            });

            await RunWithTimeout("CacheCleanup", 1000000, async () =>
            {
                var x = Next();
                var gref = Clients[0][0].GetGrainRef(x);

                // put grain into cluster A 
                var id = Clients[0][0].GetRuntimeId(x);

                WriteLog("Grain {0} at {1}", gref, id);
                Assert.Contains(Clusters[ClusterNames[0]].Silos, silo => silo.SiloAddress.ToString() == id);

                // access the grain from B, causing
                // causing entry to be installed in B's directory and/or B's directory caches
                var id2 = Clients[1][0].GetRuntimeId(x);
                AssertEqual(id2, id, gref);

                // block communication from A to B
                BlockAllClusterCommunication(ClusterNames[0], ClusterNames[1]);

                // change multi-cluster configuration to be only { B }, i.e. exclude A
                WriteLog($"Removing A from multi-cluster");
                Clients[1][0].InjectClusterConfiguration(new string[] { ClusterNames[1] }, "exclude A", false);
                WaitForMultiClusterGossipToStabilizeAsync(false).WaitWithThrow(TimeSpan.FromMinutes(System.Diagnostics.Debugger.IsAttached ? 60 : 1));

                // give the cache cleanup process time to remove all cached references in B
                await Task.Delay(40000);

                // now try to access the grain from cluster B
                // if everything works correctly this should succeed, creating a new instances on B locally
                // (if invalid caches were not removed this times out)
                WriteLog("Grain {0} doubly-activating.", gref);
                var val = Clients[1][0].CallGrain(x);
                AssertEqual(1, val, gref);
                var newid = Clients[1][0].GetRuntimeId(x);
                WriteLog("{2} sees Grain {0} at {1}", gref, newid, ClusterNames[1]);
                Assert.Contains(newid, Clusters[ClusterNames[1]].Silos.Select(s => s.SiloAddress.ToString()));
            });
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task CacheCleanupMultiple()
        {
            await RunWithTimeout("Start Clusters and Clients", 180 * 1000, () =>
            {
                return StartClustersAndClients(3, 3);
            });

            await RunWithTimeout("CacheCleanupMultiple", 1000000, async () =>
            {
                int count = 100;

                var grains = Next(count);
                var grefs = grains.Select((x) => Clients[0][0].GetGrainRef(x)).ToList();

                // put grains into cluster A 
                var ids = grains.Select((x,i) => Clients[0][i % 3].GetRuntimeId(x)).ToList();
                WriteLog($"{count} Grains activated on A");
                for (int i = 0; i < count; i++ )
                   Assert.Contains(Clusters[ClusterNames[0]].Silos, silo => silo.SiloAddress.ToString() == ids[i]);

                // access all the grains living in A from all the clients of B
                // causing  entries to be installed in B's directory and in B's directory caches
                for (int j = 0; j < 3; j++)
                {
                    var ids2 = grains.Select((x) => Clients[1][j].GetRuntimeId(x)).ToList();
                    for (int i = 0; i < count; i++)
                        AssertEqual(ids2[i], ids[i], grefs[i]);
                }
                WriteLog($"{count} Grain references cached in B");

                // block communication from A to B
                BlockAllClusterCommunication(ClusterNames[0], ClusterNames[1]);

                // change multi-cluster configuration to be only { B }, i.e. exclude A
                WriteLog($"Removing A from multi-cluster");
                Clients[1][0].InjectClusterConfiguration(new string[] { ClusterNames[1] }, "exclude A", false);
                WaitForMultiClusterGossipToStabilizeAsync(false).WaitWithThrow(TimeSpan.FromMinutes(System.Diagnostics.Debugger.IsAttached ? 60 : 1));

                // give the cache cleanup process time to remove all cached references in B
                await Task.Delay(50000);

                // call the grains from random clients of B
                // if everything works correctly this should succeed, creating new instances on B locally
                // (if invalid caches were not removed this times out)
                var vals = grains.Select((x) => Clients[1][Math.Abs(x) % 3].CallGrain(x)).ToList();
                for (int i = 0; i < count; i++)
                    AssertEqual(1, vals[i], grefs[i]);

                // check the placement of the new grains, from client 0
                var newids = grains.Select((x) => Clients[1][0].GetRuntimeId(x)).ToList();
                for (int i = 0; i < count; i++)
                    Assert.Contains(newids[i], Clusters[ClusterNames[1]].Silos.Select(s => s.SiloAddress.ToString()));

                // check the placement of these same grains, from other clients
                for (int j = 1; j < 3; j++)
                {
                    var ids2 = grains.Select((x) => Clients[1][j].GetRuntimeId(x)).ToList();
                    for (int i = 0; i < count; i++)
                        AssertEqual(ids2[i], newids[i], grefs[i]);
                }
            });
        }
    }
}
#endif