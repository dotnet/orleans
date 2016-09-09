using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using Tester;
using Xunit;
using Xunit.Abstractions;

namespace Tests.GeoClusterTests
{
    /// <summary>
    /// A utility class for tests that include multiple clusters.
    /// It calls static methods on TestingSiloHost for starting and stopping silos.
    /// </summary>
    public class TestingClusterHost : IDisposable
    {
        ITestOutputHelper output;

        protected readonly Dictionary<string, ClusterInfo> Clusters;
        private TestingSiloHost siloHost;

        private TimeSpan gossipStabilizationTime;

        public TestingClusterHost(ITestOutputHelper output)
        {
            Clusters = new Dictionary<string, ClusterInfo>();
            this.output = output;
            TestUtils.CheckForAzureStorage();
        }

        protected struct ClusterInfo
        {
            public List<SiloHandle> Silos;  // currently active silos
            public int SequenceNumber; // we number created clusters in order of creation
        }

        public void WriteLog(string format, params object[] args)
        {
            output.WriteLine("{0} {1}", DateTime.UtcNow, string.Format(format, args));
        }

        public async Task RunWithTimeout(string name, int msec, Func<Task> test)
        {
            WriteLog("--- Starting {0}", name);
            var stopwatch = new System.Diagnostics.Stopwatch();
            stopwatch.Start();
            var testtask = test();
            await Task.WhenAny(testtask, Task.Delay(System.Diagnostics.Debugger.IsAttached ? 3600000 : msec));
            stopwatch.Stop();
            if (!testtask.IsCompleted)
            {
                WriteLog("--- {0} Timed out after {1})", name, stopwatch.Elapsed);
                Assert.True(false, string.Format("{0} took too long, timed out", name));
            }
            try // see if there was an exception and print it for logging
            {
                await testtask;
                WriteLog("--- {0} Done (elapsed = {1})", name, stopwatch.Elapsed);
            }
            catch (Exception e)
            {
                WriteLog("--- Exception observed in {0}: {1})", name, e);
                throw e;
            }
        }

        public void AssertEqual<T>(T expected, T actual, string comment) 
        {
            try
            {
                Assert.Equal(expected, actual);
            }
            catch (Exception e)
            {
                WriteLog("Equality assertion failed; expected={0}, actual={1} comment={2}", expected, actual, comment);
                throw e;
            }
        }

        /// <summary>
        /// Wait for the multicluster-gossip sub-system to stabilize.
        /// </summary>
        public async Task WaitForMultiClusterGossipToStabilizeAsync(bool account_for_lost_messages)
        {
            TimeSpan stabilizationTime = account_for_lost_messages ? gossipStabilizationTime : TimeSpan.FromSeconds(1);
            WriteLog("WaitForMultiClusterGossipToStabilizeAsync is about to sleep for {0}", stabilizationTime);
            await Task.Delay(stabilizationTime);
            WriteLog("WaitForMultiClusterGossipToStabilizeAsync is done sleeping");
        }

        public Task WaitForLivenessToStabilizeAsync()
        {
            return this.siloHost.WaitForLivenessToStabilizeAsync();
        }

        private static TimeSpan GetGossipStabilizationTime(GlobalConfiguration global)
        {
            TimeSpan stabilizationTime = TimeSpan.Zero;

            stabilizationTime += TimeSpan.FromMilliseconds(global.BackgroundGossipInterval.TotalMilliseconds * 1.05 + 50);

            return stabilizationTime;
        }

        public void StopSilo(SiloHandle instance)
        {
            siloHost.StopSilo(instance);
        }
        public void KillSilo(SiloHandle instance)
        {
            siloHost.KillSilo(instance);
        }

        public void StopAllSilos()
        {
            siloHost.StopAllSilos();
        }

        public ParallelOptions paralleloptions = new ParallelOptions() { MaxDegreeOfParallelism = 4 };

        #region Default Cluster and Client Configuration

        private static int GetPortBase(int clusternumber)
        {
            return 21000 + (clusternumber + 1) * 100;
        }
        private static int GetProxyBase(int clusternumber)
        {
            return 22000 + (clusternumber + 2) * 100;
        }

        #endregion

        #region Cluster Creation

        public void NewGeoCluster(Guid globalServiceId, string clusterId, int numSilos, Action<ClusterConfiguration> customizer = null)
        {
            Action<ClusterConfiguration> extendedcustomizer = config =>
                {
                    // configure multi-cluster network
                    config.Globals.ServiceId = globalServiceId;
                    config.Globals.ClusterId = clusterId;
                    config.Globals.MaxMultiClusterGateways = 2;
                    config.Globals.DefaultMultiCluster = null;

                    config.Globals.GossipChannels = new List<GlobalConfiguration.GossipChannelConfiguration>(1) {
                          new GlobalConfiguration.GossipChannelConfiguration()
                          {
                              ChannelType = GlobalConfiguration.GossipChannelType.AzureTable,
                              ConnectionString = StorageTestConstants.DataConnectionString
                          }};

                    customizer?.Invoke(config);
                };

            NewCluster(clusterId, numSilos, extendedcustomizer);
        }


        public void NewCluster(string clusterId, int numSilos, Action<ClusterConfiguration> customizer = null)
        {
            lock (Clusters)
            {
                var myCount = Clusters.Count;

                WriteLog("Starting Cluster {0}  ({1})...", myCount, clusterId);

                if (myCount == 0)
                {
                    TestingSiloHost.StopAllSilosIfRunning();
                    this.siloHost = TestingSiloHost.CreateUninitialized();
                }

                var silohandles = new SiloHandle[numSilos];

                var options = new TestingSiloOptions
                {
                    StartClient = false,
                    AdjustConfig = customizer,
                    BasePort = GetPortBase(myCount),
                    ProxyBasePort = GetProxyBase(myCount)
                };
                silohandles[0] = TestingSiloHost.StartOrleansSilo(this.siloHost, Silo.SiloType.Primary, options, 0);

                Parallel.For(1, numSilos, paralleloptions, i =>
                {
                    silohandles[i] = TestingSiloHost.StartOrleansSilo(this.siloHost, Silo.SiloType.Secondary, options, i);
                });

                Clusters[clusterId] = new ClusterInfo
                {
                    Silos = silohandles.ToList(),
                    SequenceNumber = myCount
                };

                if (myCount == 0)
                    gossipStabilizationTime = GetGossipStabilizationTime(silohandles[0].Silo.GlobalConfig);

                WriteLog("Cluster {0} started. [{1}]", clusterId, string.Join(" ", silohandles.Select(s => s.ToString())));
            }
        }

        public void AddSiloToCluster(string clusterId, string siloName, Action<ClusterConfiguration> customizer = null)
        {
            var clusterinfo = Clusters[clusterId];

            var options = new TestingSiloOptions
            {
                StartClient = false,
                AdjustConfig = customizer
            };

            var silo = TestingSiloHost.StartOrleansSilo(this.siloHost, Silo.SiloType.Secondary, options, clusterinfo.Silos.Count);
        }
        public virtual void Dispose()
        {
            StopAllClientsAndClusters();
        }


        public void StopAllClientsAndClusters()
        {
            WriteLog("Stopping all Clients and Clusters...");
            var stopwatch = new System.Diagnostics.Stopwatch();
            stopwatch.Start();

            try
            {
                var disposetask = Task.Run(() => {
                    StopAllClients();
                    WriteLog("All Clients are Stopped.");
                    StopAllClusters();
                    WriteLog("All Clusters are Stopped.");
                });
           

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

        public void StopAllClusters()
        {
            lock (Clusters)
            {
                Parallel.ForEach(Clusters.Keys, paralleloptions, key =>
                {
                    var info = Clusters[key];
                    Parallel.For(1, info.Silos.Count, i => siloHost.StopSilo(info.Silos[i]));
                    siloHost.StopSilo(info.Silos[0]);
                });
                Clusters.Clear();
            }
        }

        #endregion

        #region client wrappers

        private readonly List<AppDomain> activeClients = new List<AppDomain>();


        // The following is a base class to use for creating client wrappers.
        // We use ClientWrappers to load an Orleans client in its own app domain. 
        // This allows us to create multiple clients that are connected to different silos.
        public class ClientWrapperBase : MarshalByRefObject {

            public string Name { get; private set; }

            static Lazy<ClientConfiguration> clientconfiguration = new Lazy<ClientConfiguration>(() => ClientConfiguration.LoadFromFile("ClientConfigurationForTesting.xml"));

            public ClientWrapperBase(string name, int gatewayport, string clusterId, Action<ClientConfiguration> clientconfig_customizer)
            {
                this.Name = name;

                Console.WriteLine("Initializing client {0} in AppDomain {1}", name, AppDomain.CurrentDomain.FriendlyName);

                ClientConfiguration config = null;
                try
                {
                    config = clientconfiguration.Value;
                }
                catch (Exception) { }

                if (config == null)
                {
                    Assert.True(false, "Error loading client configuration file");
                }
                config.GatewayProvider = ClientConfiguration.GatewayProviderType.Config;
                config.Gateways.Clear();
                config.Gateways.Add(new IPEndPoint(IPAddress.Loopback, gatewayport));

                clientconfig_customizer?.Invoke(config);

                GrainClient.Initialize(config);
            }
        }

        // Create a client, loaded in a new app domain.
        public T NewClient<T>(string ClusterId, int ClientNumber, Action<ClientConfiguration> customizer = null) where T : ClientWrapperBase
        {
            var ci = Clusters[ClusterId];
            var name = string.Format("Client-{0}-{1}", ClusterId, ClientNumber);

            // clients are assigned to silos round-robin
            var gatewayport = ci.Silos[ClientNumber % ci.Silos.Count].GatewayPort;

            WriteLog("Starting {0} connected to {1}", name, gatewayport);

            var clientArgs = new object[] { name, gatewayport.Value, ClusterId, customizer };
            var setup = new AppDomainSetup { ApplicationBase = Environment.CurrentDirectory };
            var clientDomain = AppDomain.CreateDomain(name, null, setup);

            T client = (T)clientDomain.CreateInstanceFromAndUnwrap(
                    Assembly.GetExecutingAssembly().Location, typeof(T).FullName, false,
                    BindingFlags.Default, null, clientArgs, CultureInfo.CurrentCulture,
                    new object[] { });

            lock (activeClients)
            {
                activeClients.Add(clientDomain);
            }

            WriteLog("Started {0} connected", name);

            return client;
        }

        public void StopAllClients()
        {
            List<AppDomain> ac;

            lock (activeClients)
            {
                ac = activeClients.ToList();
                activeClients.Clear();
            }

            Parallel.For(0, ac.Count, paralleloptions, (i) =>
            {
                try
                {
                    WriteLog("Unloading client {0}", i);

                    AppDomain.Unload(ac[i]);
                }
                catch (Exception e)
                {
                    WriteLog("Exception Caught While Unloading AppDomain for client {0}: {1}", i, e);
                }
            });
        }

        #endregion

        public void BlockAllClusterCommunication(string from, string to)
        {
            foreach (var silo in Clusters[from].Silos)
                foreach (var dest in Clusters[to].Silos)
                {
                    WriteLog("Blocking {0}->{1}", silo, dest);
                    silo.Silo.TestHook.BlockSiloCommunication(dest.Endpoint, 100);
                }
        }

        public void UnblockAllClusterCommunication(string from)
        {
            foreach (var silo in Clusters[from].Silos)
            {
                WriteLog("Unblocking {0}", silo);
                silo.Silo.TestHook.UnblockSiloCommunication();
            }
        }
  
        private SiloHandle GetActiveSiloInClusterByName(string clusterId, string siloName)
        {
            if (Clusters[clusterId].Silos == null) return null;
            return Clusters[clusterId].Silos.Find(s => s.Name == siloName);
        }
    }
}