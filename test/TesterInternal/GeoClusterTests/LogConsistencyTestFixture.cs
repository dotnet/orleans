using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.LogConsistency;
using Orleans.TestingHost;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using TestExtensions;
using Orleans.EventSourcing.Common;
using Tester;

namespace Tests.GeoClusterTests
{
    /// <summary>
    ///  A fixture that provides a collection of semantic tests for log-consistency providers
    ///  (concurrent reading and updating, update propagation, conflict resolution)
    ///  on a multicluster with the desired number of clusters
    /// </summary>
    public class LogConsistencyTestFixture : IDisposable
    {
        TestingClusterHost _hostedMultiCluster;

        public TestingClusterHost MultiCluster
        {
            get { return _hostedMultiCluster ?? (_hostedMultiCluster = new TestingClusterHost()); }
        }

        public void EnsurePreconditionsMet()
        {
            TestUtils.CheckForAzureStorage();
        }

        public class ClientWrapper : Tests.GeoClusterTests.TestingClusterHost.ClientWrapperBase
        {
            public static readonly Func<string, int, string, Action<ClientConfiguration>, Action<IClientBuilder>, ClientWrapper> Factory =
                (name, gwPort, clusterId, configUpdater, clientConfigurator) => new ClientWrapper(name, gwPort, clusterId, configUpdater, clientConfigurator);

            public ClientWrapper(string name, int gatewayport, string clusterId, Action<ClientConfiguration> customizer, Action<IClientBuilder> clientConfigurator)
               : base(name, gatewayport, clusterId, customizer, clientConfigurator)
            {
                systemManagement = this.GrainFactory.GetGrain<IManagementGrain>(0);
            }

            public string GetGrainRef(string grainclass, int i)
            {
                return this.GrainFactory.GetGrain<UnitTests.GrainInterfaces.ILogTestGrain>(i, grainclass).ToString();
            }

            public void SetALocal(string grainclass, int i, int a)
            {
                var grainRef = this.GrainFactory.GetGrain<UnitTests.GrainInterfaces.ILogTestGrain>(i, grainclass);
                grainRef.SetALocal(a).GetResult();
            }

            public void SetAGlobal(string grainclass, int i, int a)
            {
                var grainRef = this.GrainFactory.GetGrain<UnitTests.GrainInterfaces.ILogTestGrain>(i, grainclass);
                grainRef.SetAGlobal(a).GetResult();
            }

            public Tuple<int, bool> SetAConditional(string grainclass, int i, int a)
            {
                var grainRef = this.GrainFactory.GetGrain<UnitTests.GrainInterfaces.ILogTestGrain>(i, grainclass);
                return grainRef.SetAConditional(a).GetResult();
            }

            public void IncrementAGlobal(string grainclass, int i)
            {
                var grainRef = this.GrainFactory.GetGrain<UnitTests.GrainInterfaces.ILogTestGrain>(i, grainclass);
                grainRef.IncrementAGlobal().GetResult();
            }

            public void IncrementALocal(string grainclass, int i)
            {
                var grainRef = this.GrainFactory.GetGrain<UnitTests.GrainInterfaces.ILogTestGrain>(i, grainclass);
                grainRef.IncrementALocal().GetResult();
            }

            public int GetAGlobal(string grainclass, int i)
            {
                var grainRef = this.GrainFactory.GetGrain<UnitTests.GrainInterfaces.ILogTestGrain>(i, grainclass);
                return grainRef.GetAGlobal().GetResult();
            }

            public int GetALocal(string grainclass, int i)
            {
                var grainRef = this.GrainFactory.GetGrain<UnitTests.GrainInterfaces.ILogTestGrain>(i, grainclass);
                return grainRef.GetALocal().GetResult();
            }    

            public void AddReservationLocal(string grainclass, int i, int x)
            {
                var grainRef = this.GrainFactory.GetGrain<UnitTests.GrainInterfaces.ILogTestGrain>(i, grainclass);
                grainRef.AddReservationLocal(x).GetResult();
            }

            public void RemoveReservationLocal(string grainclass, int i, int x)
            {
                var grainRef = this.GrainFactory.GetGrain<UnitTests.GrainInterfaces.ILogTestGrain>(i, grainclass);
                grainRef.RemoveReservationLocal(x).GetResult();
            }

            public int[] GetReservationsGlobal(string grainclass, int i)
            {
                var grainRef = this.GrainFactory.GetGrain<UnitTests.GrainInterfaces.ILogTestGrain>(i, grainclass);
                return grainRef.GetReservationsGlobal().GetResult();
            }

            public void Synchronize(string grainclass, int i)
            {
                var grainRef = this.GrainFactory.GetGrain<UnitTests.GrainInterfaces.ILogTestGrain>(i, grainclass);
                grainRef.SynchronizeGlobalState().GetResult();
            }

            public void InjectClusterConfiguration(params string[] clusters)
            {
                systemManagement.InjectMultiClusterConfiguration(clusters).GetResult();
            }
            IManagementGrain systemManagement;

            public long GetConfirmedVersion(string grainclass, int i)
            {
                var grainRef = this.GrainFactory.GetGrain<UnitTests.GrainInterfaces.ILogTestGrain>(i, grainclass);
                return grainRef.GetConfirmedVersion().GetResult();
            }

            public IEnumerable<ConnectionIssue> GetUnresolvedConnectionIssues(string grainclass, int i)
            {
                var grainRef = this.GrainFactory.GetGrain<UnitTests.GrainInterfaces.ILogTestGrain>(i, grainclass);
                return grainRef.GetUnresolvedConnectionIssues().GetResult();
            }

        }


        public void StartClustersIfNeeded(int numclusters, ITestOutputHelper output)
        {
            this.output = output;

            if (MultiCluster.Clusters.Count != numclusters)
            {
                if (MultiCluster.Clusters.Count > 0)
                    MultiCluster.StopAllClientsAndClusters();


                output.WriteLine("Creating {0} clusters and clients...", numclusters);
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
                    MultiCluster.NewGeoCluster(globalserviceid, clustername, 1,
                        cfg => LogConsistencyProviderConfiguration.ConfigureLogConsistencyProvidersForTesting(TestDefaultConfiguration.DataConnectionString, cfg));
                    Client[i] = this.MultiCluster.NewClient(clustername, 0, ClientWrapper.Factory);
                }

                output.WriteLine("Clusters and clients are ready (elapsed = {0})", stopwatch.Elapsed);

                // wait for configuration to stabilize
                MultiCluster.WaitForLivenessToStabilizeAsync().WaitWithThrow(TimeSpan.FromMinutes(1));

                Client[0].InjectClusterConfiguration(Cluster);
                MultiCluster.WaitForMultiClusterGossipToStabilizeAsync(false).WaitWithThrow(TimeSpan.FromMinutes(System.Diagnostics.Debugger.IsAttached ? 60 : 1));

                stopwatch.Stop();
                output.WriteLine("Multicluster is ready (elapsed = {0}).", stopwatch.Elapsed);
            }
            else
            {
                output.WriteLine("Reusing existing {0} clusters and clients.", numclusters);
            }
        }

        private ITestOutputHelper output;

        public virtual void Dispose()
        {
            _hostedMultiCluster?.Dispose();
        }

        protected ClientWrapper[] Client;
        protected string[] Cluster;
        protected Random random;
        protected int numclusters;

        private const int Xyz = 333;

        private void AssertEqual<T>(T expected, T actual, string grainIdentity)
        {
            if (! expected.Equals(actual))
            {
                // need to write grain identity to output so we can search for it in the trace
                output.WriteLine($"identity of offending grain: {grainIdentity}");
                Assert.Equal(expected, actual);
            }
        }

        public async Task RunChecksOnGrainClass(string grainClass, bool may_update_in_all_clusters, int phases, ITestOutputHelper output)
        {
            var random = new SafeRandom();
            Func<int> GetRandom = () => random.Next();

            Func<Task> checker1 = () => Task.Run(() =>
            {
                int x = GetRandom();
                var grainIdentity = string.Format("grainref={0}", Client[0].GetGrainRef(grainClass, x));
                // force creation of replicas
                for (int i = 0; i < numclusters; i++) 
                   AssertEqual(0, Client[i].GetALocal(grainClass, x), grainIdentity); 
                // write global on client 0
                Client[0].SetAGlobal(grainClass, x, Xyz);
                // read global on other clients
                for (int i = 1; i < numclusters; i++)
                {
                    int r = Client[i].GetAGlobal(grainClass, x);
                    AssertEqual(Xyz, r, grainIdentity);
                }
                // check local stability
                for (int i = 0; i < numclusters; i++)
                    AssertEqual(Xyz, Client[i].GetALocal(grainClass, x), grainIdentity);
                // check versions
                for (int i = 0; i < numclusters; i++)
                    AssertEqual(1, Client[i].GetConfirmedVersion(grainClass, x), grainIdentity);
            });

            Func<Task> checker2 = () => Task.Run(() =>
            {
                int x = GetRandom();
                var grainIdentity = string.Format("grainref={0}", Client[0].GetGrainRef(grainClass, x));
                // increment on replica 0
                Client[0].IncrementAGlobal(grainClass, x);
                // expect on other replicas
                for (int i = 1; i < numclusters; i++)
                {
                    int r = Client[i].GetAGlobal(grainClass, x);
                    AssertEqual(1, r, grainIdentity);
                }
                // check versions
                for (int i = 0; i < numclusters; i++)
                    AssertEqual(1, Client[i].GetConfirmedVersion(grainClass, x), grainIdentity);
            });

            Func<Task> checker2b = () => Task.Run(() =>
            {
                int x = GetRandom();
                var grainIdentity = string.Format("grainref={0}", Client[0].GetGrainRef(grainClass, x));
                // force first creation on replica 1
                AssertEqual(0, Client[1].GetAGlobal(grainClass, x), grainIdentity);
                // increment on replica 0
                Client[0].IncrementAGlobal(grainClass, x);
                // expect on other replicas
                for (int i = 1; i < numclusters; i++)
                {
                    int r = Client[i].GetAGlobal(grainClass, x);
                    AssertEqual(1, r, grainIdentity);
                }
                // check versions
                for (int i = 0; i < numclusters; i++)
                    AssertEqual(1, Client[i].GetConfirmedVersion(grainClass, x), grainIdentity);
            });

            Func<int, Task> checker3 = (int numupdates) => Task.Run(() =>
            {
                int x = GetRandom();
                var grainIdentity = string.Format("grainref={0}", Client[0].GetGrainRef(grainClass, x));

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
                AssertEqual(numupdates, Client[0].GetAGlobal(grainClass, x), grainIdentity); 

                for (int i = 1; i < numclusters; i++)
                    AssertEqual(numupdates, Client[i].GetAGlobal(grainClass, x), grainIdentity); // get all

                // check versions
                for (int i = 0; i < numclusters; i++)
                    AssertEqual(numupdates, Client[i].GetConfirmedVersion(grainClass, x), grainIdentity);
            });

            Func<Task> checker4 = () => Task.Run(() =>
            {
                int x = GetRandom();
                var grainIdentity = string.Format("grainref={0}", Client[0].GetGrainRef(grainClass, x));
                var t = new List<Task>();
                for (int i = 0; i < numclusters; i++)
                {
                    int c = i;
                    t.Add(Task.Run(() => AssertEqual(true, Client[c].GetALocal(grainClass, x) == 0, grainIdentity)));
                }
                for (int i = 0; i < numclusters; i++)
                {
                    int c = i;
                    t.Add(Task.Run(() => AssertEqual(true, Client[c].GetAGlobal(grainClass, x) == 0, grainIdentity)));
                }
                return Task.WhenAll(t);
            });

            Func<Task> checker5 = () => Task.Run(() =>
            {
                var x = GetRandom();
                var grainIdentity = string.Format("grainref={0}", Client[0].GetGrainRef(grainClass, x));
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
                AssertEqual(1, result.Length, grainIdentity);
                AssertEqual(2, result[0], grainIdentity);
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

                Assert.True(done.All(b => b), string.Format("checker6({0}): update did not propagate within 20 sec", preload));
            };

            Func<int, Task> checker7 = (int variation) => Task.Run(async () =>
            {
                int x = GetRandom();

                if (variation % 2 == 0)
                    Client[1].GetAGlobal(grainClass, x);
                if ((variation / 2) % 2 == 0)
                    Client[0].GetAGlobal(grainClass, x);

                var grainIdentity = string.Format("grainref={0}", Client[0].GetGrainRef(grainClass, x));

                // write conditional on client 0, should always succeed
                {
                    var result = Client[0].SetAConditional(grainClass, x, Xyz);
                    AssertEqual(0, result.Item1, grainIdentity);
                    AssertEqual(true, result.Item2, grainIdentity);
                    AssertEqual(1, Client[0].GetConfirmedVersion(grainClass, x), grainIdentity);
                }

                if ((variation / 4) % 2 == 1)
                    await Task.Delay(100);

                // write conditional on Client[1], may or may not succeed based on timing
                {
                    var result = Client[1].SetAConditional(grainClass, x, 444);
                    if (result.Item1 == 0) // was stale, thus failed
                    {
                        AssertEqual(false, result.Item2, grainIdentity);
                        // must have updated as a result
                        AssertEqual(1, Client[1].GetConfirmedVersion(grainClass, x), grainIdentity);
                        // check stability
                        AssertEqual(Xyz, Client[0].GetALocal(grainClass, x), grainIdentity);
                        AssertEqual(Xyz, Client[1].GetALocal(grainClass, x), grainIdentity);
                        AssertEqual(Xyz, Client[0].GetAGlobal(grainClass, x), grainIdentity);
                        AssertEqual(Xyz, Client[1].GetAGlobal(grainClass, x), grainIdentity);
                    }
                    else // was up-to-date, thus succeeded
                    {
                        AssertEqual(true, result.Item2, grainIdentity);
                        AssertEqual(1, result.Item1, grainIdentity);
                        // version is now 2
                        AssertEqual(2, Client[1].GetConfirmedVersion(grainClass, x), grainIdentity);
                        // check stability
                        AssertEqual(444, Client[1].GetALocal(grainClass, x), grainIdentity);
                        AssertEqual(444, Client[0].GetAGlobal(grainClass, x), grainIdentity);
                        AssertEqual(444, Client[1].GetAGlobal(grainClass, x), grainIdentity);
                    }
                }
            });

            output.WriteLine("Running individual short tests");

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
                MultiCluster.SetProtocolMessageFilterForTesting(Cluster[0], msg => ! (msg is INotificationMessage));
                await checker7(0);
                await checker7(1);
                await checker7(2);
                await checker7(3);
                MultiCluster.SetProtocolMessageFilterForTesting(Cluster[0], _ => true);
            }

            output.WriteLine("Running individual longer tests");

            // then, run slightly longer tests
            if (phases != 0)
            {
                await checker3(20);
                await checker3(phases);
            }

            output.WriteLine("Running many concurrent test instances");

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
