using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.LogConsistency;
using Orleans.TestingHost;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnitTests.GrainInterfaces;
using Xunit;
using Xunit.Abstractions;
using TestExtensions;
using Orleans.EventSourcing.Common;

namespace Tests.GeoClusterTests
{
    /// <summary>
    ///  A fixture that provides a collection of semantic tests for log-consistency providers
    ///  (concurrent reading and updating, update propagation, conflict resolution)
    ///  on a multicluster with the desired number of clusters
    /// </summary>
    public class LogConsistencyTestFixture : TestingClusterHost
    {

        #region client wrappers

        public class ClientWrapper : Tests.GeoClusterTests.TestingClusterHost.ClientWrapperBase
        {
            public ClientWrapper(string name, int gatewayport, string clusterId, Action<ClientConfiguration> customizer)
               : base(name, gatewayport, clusterId, customizer)
            {
                systemManagement = GrainClient.GrainFactory.GetGrain<IManagementGrain>(0);
            }

            public string GetGrainRef(string grainclass, int i)
            {
                return GrainClient.GrainFactory.GetGrain<UnitTests.GrainInterfaces.ILogConsistentGrain>(i, grainclass).ToString();
            }

            public void SetALocal(string grainclass, int i, int a)
            {
                var grainRef = GrainClient.GrainFactory.GetGrain<UnitTests.GrainInterfaces.ILogConsistentGrain>(i, grainclass);
                grainRef.SetALocal(a).GetResult();
            }

            public void SetAGlobal(string grainclass, int i, int a)
            {
                var grainRef = GrainClient.GrainFactory.GetGrain<UnitTests.GrainInterfaces.ILogConsistentGrain>(i, grainclass);
                grainRef.SetAGlobal(a).GetResult();
            }

            public Tuple<int, bool> SetAConditional(string grainclass, int i, int a)
            {
                var grainRef = GrainClient.GrainFactory.GetGrain<UnitTests.GrainInterfaces.ILogConsistentGrain>(i, grainclass);
                return grainRef.SetAConditional(a).GetResult();
            }

            public void IncrementAGlobal(string grainclass, int i)
            {
                var grainRef = GrainClient.GrainFactory.GetGrain<UnitTests.GrainInterfaces.ILogConsistentGrain>(i, grainclass);
                grainRef.IncrementAGlobal().GetResult();
            }

            public void IncrementALocal(string grainclass, int i)
            {
                var grainRef = GrainClient.GrainFactory.GetGrain<UnitTests.GrainInterfaces.ILogConsistentGrain>(i, grainclass);
                grainRef.IncrementALocal().GetResult();
            }

            public int GetAGlobal(string grainclass, int i)
            {
                var grainRef = GrainClient.GrainFactory.GetGrain<UnitTests.GrainInterfaces.ILogConsistentGrain>(i, grainclass);
                return grainRef.GetAGlobal().GetResult();
            }

            public int GetALocal(string grainclass, int i)
            {
                var grainRef = GrainClient.GrainFactory.GetGrain<UnitTests.GrainInterfaces.ILogConsistentGrain>(i, grainclass);
                return grainRef.GetALocal().GetResult();
            }    

            public void AddReservationLocal(string grainclass, int i, int x)
            {
                var grainRef = GrainClient.GrainFactory.GetGrain<UnitTests.GrainInterfaces.ILogConsistentGrain>(i, grainclass);
                grainRef.AddReservationLocal(x).GetResult();
            }

            public void RemoveReservationLocal(string grainclass, int i, int x)
            {
                var grainRef = GrainClient.GrainFactory.GetGrain<UnitTests.GrainInterfaces.ILogConsistentGrain>(i, grainclass);
                grainRef.RemoveReservationLocal(x).GetResult();
            }

            public int[] GetReservationsGlobal(string grainclass, int i)
            {
                var grainRef = GrainClient.GrainFactory.GetGrain<UnitTests.GrainInterfaces.ILogConsistentGrain>(i, grainclass);
                return grainRef.GetReservationsGlobal().GetResult();
            }

            public void Synchronize(string grainclass, int i)
            {
                var grainRef = GrainClient.GrainFactory.GetGrain<UnitTests.GrainInterfaces.ILogConsistentGrain>(i, grainclass);
                grainRef.SynchronizeGlobalState().GetResult();
            }

            public void InjectClusterConfiguration(params string[] clusters)
            {
                systemManagement.InjectMultiClusterConfiguration(clusters).GetResult();
            }
            IManagementGrain systemManagement;

            public long GetConfirmedVersion(string grainclass, int i)
            {
                var grainRef = GrainClient.GrainFactory.GetGrain<UnitTests.GrainInterfaces.ILogConsistentGrain>(i, grainclass);
                return grainRef.GetConfirmedVersion().GetResult();
            }

            public IEnumerable<ConnectionIssue> GetUnresolvedConnectionIssues(string grainclass, int i)
            {
                var grainRef = GrainClient.GrainFactory.GetGrain<UnitTests.GrainInterfaces.ILogConsistentGrain>(i, grainclass);
                return grainRef.GetUnresolvedConnectionIssues().GetResult();
            }

        }

        #endregion


        public void StartClustersIfNeeded(int numclusters, ITestOutputHelper output)
        {
            this.output = output;

            if (Clusters.Count != numclusters)
            {
                if (Clusters.Count > 0)
                    StopAllClientsAndClusters();


                WriteLog("Creating {0} clusters and clients...", numclusters);
                this.numclusters = numclusters;

                Assert.True(numclusters >= 2);

                var stopwatch = new System.Diagnostics.Stopwatch();
                stopwatch.Start();

                // use a random global service id for testing purposes
                var globalserviceid = Guid.NewGuid();
                random = new Random();

                System.Threading.ThreadPool.SetMaxThreads(8, 8);

                // Create clusters and clients
                Cluster = new string[numclusters];
                Client = new ClientWrapper[numclusters];
                for (int i = 0; i < numclusters; i++)
                {
                    var clustername = Cluster[i] = ((char)('A' + i)).ToString();
                    NewGeoCluster(globalserviceid, clustername, 1,
                        cfg => LogConsistencyProviderConfiguration.ConfigureLogConsistencyProvidersForTesting(TestDefaultConfiguration.DataConnectionString, cfg));
                    Client[i] = NewClient<ClientWrapper>(clustername, 0);
                }

                WriteLog("Clusters and clients are ready (elapsed = {0})", stopwatch.Elapsed);

                // wait for configuration to stabilize
                WaitForLivenessToStabilizeAsync().WaitWithThrow(TimeSpan.FromMinutes(1));

                Client[0].InjectClusterConfiguration(Cluster);
                WaitForMultiClusterGossipToStabilizeAsync(false).WaitWithThrow(TimeSpan.FromMinutes(System.Diagnostics.Debugger.IsAttached ? 60 : 1));

                stopwatch.Stop();
                WriteLog("Multicluster is ready (elapsed = {0}).", stopwatch.Elapsed);
            }
            else
            {
                WriteLog("Reusing existing {0} clusters and clients.", numclusters);
            }
        }

        public override void Dispose()
        {
            base.output = null; // cannot trace during dispose from fixtures
            base.Dispose();
        }

        protected ClientWrapper[] Client;
        protected string[] Cluster;
        protected Random random;
        protected int numclusters;

        private const int Xyz = 333;

        public async Task RunChecksOnGrainClass(string grainClass, bool may_update_in_all_clusters, int phases)
        {
            Random random = new Random();

            Func<int> GetRandom = () =>
            {
                lock (random)
                    return random.Next();
            };

            Func<Task> checker1 = () => Task.Run(() =>
            {
                int x = GetRandom();
                var grainidentity = string.Format("grainref={0}", Client[0].GetGrainRef(grainClass, x));
                // force creation of replicas
                for (int i = 0; i < numclusters; i++) 
                   AssertEqual(0, Client[i].GetALocal(grainClass, x), grainidentity); 
                // write global on client 0
                Client[0].SetAGlobal(grainClass, x, Xyz);
                // read global on other clients
                for (int i = 1; i < numclusters; i++)
                {
                    int r = Client[i].GetAGlobal(grainClass, x);
                    AssertEqual(Xyz, r, grainidentity);
                }
                // check local stability
                for (int i = 0; i < numclusters; i++)
                    AssertEqual(Xyz, Client[i].GetALocal(grainClass, x), grainidentity);
                // check versions
                for (int i = 0; i < numclusters; i++)
                    AssertEqual(1, Client[i].GetConfirmedVersion(grainClass, x), grainidentity);
            });

            Func<Task> checker2 = () => Task.Run(() =>
            {
                int x = GetRandom();
                var grainidentity = string.Format("grainref={0}", Client[0].GetGrainRef(grainClass, x));
                // increment on replica 0
                Client[0].IncrementAGlobal(grainClass, x);
                // expect on other replicas
                for (int i = 1; i < numclusters; i++)
                {
                    int r = Client[i].GetAGlobal(grainClass, x);
                    AssertEqual(1, r, grainidentity);
                }
                // check versions
                for (int i = 0; i < numclusters; i++)
                    AssertEqual(1, Client[i].GetConfirmedVersion(grainClass, x), grainidentity);
            });

            Func<Task> checker2b = () => Task.Run(() =>
            {
                int x = GetRandom();
                var grainidentity = string.Format("grainref={0}", Client[0].GetGrainRef(grainClass, x));
                // force first creation on replica 1
                AssertEqual(0, Client[1].GetAGlobal(grainClass, x), grainidentity);
                // increment on replica 0
                Client[0].IncrementAGlobal(grainClass, x);
                // expect on other replicas
                for (int i = 1; i < numclusters; i++)
                {
                    int r = Client[i].GetAGlobal(grainClass, x);
                    AssertEqual(1, r, grainidentity);
                }
                // check versions
                for (int i = 0; i < numclusters; i++)
                    AssertEqual(1, Client[i].GetConfirmedVersion(grainClass, x), grainidentity);
            });

            Func<int, Task> checker3 = (int numupdates) => Task.Run(() =>
            {
                int x = GetRandom();
                var grainidentity = string.Format("grainref={0}", Client[0].GetGrainRef(grainClass, x));

                // concurrently chaotically increment (numupdates) times
                Parallel.For(0, numupdates, i =>
                {
                    var target = may_update_in_all_clusters ? i % numclusters : 0;
                    Client[target].IncrementALocal(grainClass, x);
                });

                if (may_update_in_all_clusters)
                {
                    for (int i = 1; i < numclusters; i++)
                        Client[i].Synchronize(grainClass, x); // push all changes
                }

                // push & get all
                AssertEqual(numupdates, Client[0].GetAGlobal(grainClass, x), grainidentity); 

                for (int i = 1; i < numclusters; i++)
                    AssertEqual(numupdates, Client[i].GetAGlobal(grainClass, x), grainidentity); // get all

                // check versions
                for (int i = 0; i < numclusters; i++)
                    AssertEqual(numupdates, Client[i].GetConfirmedVersion(grainClass, x), grainidentity);
            });

            Func<Task> checker4 = () => Task.Run(() =>
            {
                int x = GetRandom();
                var t = new List<Task>();
                for (int i = 0; i < numclusters; i++)
                {
                    int c = i;
                    t.Add(Task.Run(() => Assert.True(Client[c].GetALocal(grainClass, x) == 0)));
                }
                for (int i = 0; i < numclusters; i++)
                {
                    int c = i;
                    t.Add(Task.Run(() => Assert.True(Client[c].GetAGlobal(grainClass, x) == 0)));
                }
                return Task.WhenAll(t);
            });

            Func<Task> checker5 = () => Task.Run(() =>
            {
                var x = GetRandom();
                Task.WaitAll(
                   Task.Run(() =>
                   {
                       Client[0].AddReservationLocal(grainClass, x, 0);
                       Client[0].RemoveReservationLocal(grainClass, x, 0);
                       Client[0].Synchronize(grainClass, x);
                   }),
                 Task.Run(() =>
                 {
                     Client[1].AddReservationLocal(grainClass, x, 1);
                     Client[1].RemoveReservationLocal(grainClass, x, 1);
                     Client[1].AddReservationLocal(grainClass, x, 2);
                     Client[1].Synchronize(grainClass, x);
                 })
               );
                var result = Client[0].GetReservationsGlobal(grainClass, x);
                Assert.Equal(1, result.Length);
                Assert.Equal(2, result[0]);
            });

            Func<int, Task> checker6 = async (int preload) =>
            {
                var x = GetRandom();

                if (preload % 2 == 0)
                    Client[1].GetAGlobal(grainClass, x);
                if ((preload / 2) % 2 == 0)
                    Client[0].GetAGlobal(grainClass, x);

                bool[] done = new bool[numclusters - 1];

                var t = new List<Task>();

                // create listener tasks
                for (int i = 1; i < numclusters; i++)
                {
                    int c = i;
                    t.Add(Task.Run(async () =>
                    {
                        while (Client[c].GetALocal(grainClass, x) != 1)
                            await Task.Delay(100);
                        done[c - 1] = true;
                    }));
                }

                // send notification
                Client[0].SetALocal(grainClass, x, 1);

                await Task.WhenAny(
                    Task.Delay(20000),
                    Task.WhenAll(t)
                );

                AssertEqual(true, done.All(b => b), string.Format("checker6({0}): update did not propagate within 20 sec", preload));
            };

            Func<int, Task> checker7 = (int variation) => Task.Run(async () =>
            {
                int x = GetRandom();

                if (variation % 2 == 0)
                    Client[1].GetAGlobal(grainClass, x);
                if ((variation / 2) % 2 == 0)
                    Client[0].GetAGlobal(grainClass, x);

                var grainidentity = string.Format("grainref={0}", Client[0].GetGrainRef(grainClass, x));

                // write conditional on client 0, should always succeed
                {
                    var result = Client[0].SetAConditional(grainClass, x, Xyz);
                    AssertEqual(0, result.Item1, grainidentity);
                    AssertEqual(true, result.Item2, grainidentity);
                    AssertEqual(1, Client[0].GetConfirmedVersion(grainClass, x), grainidentity);
                }

                if ((variation / 4) % 2 == 1)
                    await Task.Delay(100);

                // write conditional on Client[1], may or may not succeed based on timing
                {
                    var result = Client[1].SetAConditional(grainClass, x, 444);
                    if (result.Item1 == 0) // was stale, thus failed
                    {
                        AssertEqual(false, result.Item2, grainidentity);
                        // must have updated as a result
                        AssertEqual(1, Client[1].GetConfirmedVersion(grainClass, x), grainidentity);
                        // check stability
                        AssertEqual(Xyz, Client[0].GetALocal(grainClass, x), grainidentity);
                        AssertEqual(Xyz, Client[1].GetALocal(grainClass, x), grainidentity);
                        AssertEqual(Xyz, Client[0].GetAGlobal(grainClass, x), grainidentity);
                        AssertEqual(Xyz, Client[1].GetAGlobal(grainClass, x), grainidentity);
                    }
                    else // was up-to-date, thus succeeded
                    {
                        AssertEqual(true, result.Item2, grainidentity);
                        AssertEqual(1, result.Item1, grainidentity);
                        // version is now 2
                        AssertEqual(2, Client[1].GetConfirmedVersion(grainClass, x), grainidentity);
                        // check stability
                        AssertEqual(444, Client[1].GetALocal(grainClass, x), grainidentity);
                        AssertEqual(444, Client[0].GetAGlobal(grainClass, x), grainidentity);
                        AssertEqual(444, Client[1].GetAGlobal(grainClass, x), grainidentity);
                    }
                }
            });

            WriteLog("Running individual short tests");

            // first, run short ones in sequence
            await checker1();
            await checker2();
            await checker2b();
            await checker3(4);
            await checker3(20);
            await checker4();

            if (may_update_in_all_clusters)
                await checker5();

            await checker6(0);
            await checker6(1);
            await checker6(2);
            await checker6(3);

            if (may_update_in_all_clusters)
            {
                await checker7(0);
                await checker7(4);
                await checker7(7);

                // run tests under blocked notification to force race one way
                SetProtocolMessageFilterForTesting(Cluster[0], msg => ! (msg is INotificationMessage));
                await checker7(0);
                await checker7(1);
                await checker7(2);
                await checker7(3);
                SetProtocolMessageFilterForTesting(Cluster[0], _ => true);
            }

            WriteLog("Running individual longer tests");

            // then, run slightly longer tests
            if (phases != 0)
            {
                await checker3(20);
                await checker3(phases);
            }

            WriteLog("Running many concurrent test instances");

            var tasks = new List<Task>();
            for (int i = 0; i < phases; i++)
            {
                tasks.Add(checker1());
                tasks.Add(checker2());
                tasks.Add(checker2b());
                tasks.Add(checker3(4));
                tasks.Add(checker4());

                if (may_update_in_all_clusters)
                    tasks.Add(checker5());

                tasks.Add(checker6(0));
                tasks.Add(checker6(1));
                tasks.Add(checker6(2));
                tasks.Add(checker6(3));

                if (may_update_in_all_clusters)
                {
                    tasks.Add(checker7(0));
                    tasks.Add(checker7(1));
                    tasks.Add(checker7(2));
                    tasks.Add(checker7(3));
                    tasks.Add(checker7(4));
                    tasks.Add(checker7(5));
                    tasks.Add(checker7(6));
                    tasks.Add(checker7(7));
                }
            }
            await Task.WhenAll(tasks);
        }
    }
}
