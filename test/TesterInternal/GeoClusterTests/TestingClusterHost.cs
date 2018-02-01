using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using Tester;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;
using Orleans.MultiCluster;
using Orleans.Runtime;
using Microsoft.Extensions.Logging;
using Orleans.TestingHost.Utils;

namespace Tests.GeoClusterTests
{
    /// <summary>
    /// A utility class for tests that include multiple clusters.
    /// </summary>
    public class TestingClusterHost : IDisposable
    {
        public readonly Dictionary<string, ClusterInfo> Clusters = new Dictionary<string, ClusterInfo>();

        protected ITestOutputHelper output;

        private TimeSpan gossipStabilizationTime = TimeSpan.FromSeconds(10);

        public TestingClusterHost(ITestOutputHelper output = null)
        {
            this.output = output;
            TestUtils.CheckForAzureStorage();
        }

        public struct ClusterInfo
        {
            public TestCluster Cluster;
            public int SequenceNumber; // we number created clusters in order of creation
            public IEnumerable<SiloHandle> Silos => Cluster.GetActiveSilos();
        }

        public void WriteLog(string format, params object[] args)
        {
            if (output != null)
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
                throw;
            }
        }

        public void AssertEqual<T>(T expected, T actual, string comment) 
        {
            try
            {
                Assert.Equal(expected, actual);
            }
            catch (Exception)
            {
                WriteLog("Equality assertion failed; expected={0}, actual={1} comment={2}", expected, actual, comment);
                throw;
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
            return this.Clusters.Any() 
                ? this.Clusters.First().Value.Cluster.WaitForLivenessToStabilizeAsync()
                : Task.Delay(gossipStabilizationTime);
        }

        private static TimeSpan GetGossipStabilizationTime(GlobalConfiguration global)
        {
            TimeSpan stabilizationTime = TimeSpan.Zero;

            stabilizationTime += TimeSpan.FromMilliseconds(global.BackgroundGossipInterval.TotalMilliseconds * 1.05 + 50);

            return stabilizationTime;
        }

        public void StopAllSilos()
        {
            foreach (var cluster in Clusters.Values)
            {
                cluster.Cluster.StopAllSilos();
            }
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

        public void NewGeoCluster(Guid globalServiceId, string clusterId, short numSilos, Action<ClusterConfiguration> customizer = null)
        {
            Action<ClusterConfiguration> extendedcustomizer = config =>
                {
                    // configure multi-cluster network
                    config.Globals.ServiceId = globalServiceId;
                    config.Globals.ClusterId = clusterId;
                    config.Globals.HasMultiClusterNetwork = true;
                    config.Globals.MaxMultiClusterGateways = 2;
                    config.Globals.DefaultMultiCluster = null;

                    config.Globals.GossipChannels = new List<GlobalConfiguration.GossipChannelConfiguration>(1) {
                          new GlobalConfiguration.GossipChannelConfiguration()
                          {
                              ChannelType = GlobalConfiguration.GossipChannelType.AzureTable,
                              ConnectionString = TestDefaultConfiguration.DataConnectionString
                          }};

                    customizer?.Invoke(config);
                };

            NewCluster(clusterId, numSilos, extendedcustomizer);
        }


        private class TestSiloBuilderConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder.ConfigureLogging(builder =>
                {
                    builder.AddFilter("Runtime.Catalog", LogLevel.Debug);
                    builder.AddFilter("Runtime.Dispatcher", LogLevel.Trace);
                    builder.AddFilter("Orleans.GrainDirectory.LocalGrainDirectory", LogLevel.Trace);
                });
            }
        }

        public void NewCluster(string clusterId, short numSilos, Action<ClusterConfiguration> customizer = null)
        {
            TestCluster testCluster;
            lock (Clusters)
            {
                var myCount = Clusters.Count;

                WriteLog("Starting Cluster {0}  ({1})...", myCount, clusterId);

                var builder = new TestClusterBuilder(initialSilosCount: numSilos)
                {
                    Options =
                    {
                        ClusterId = clusterId,
                        BaseSiloPort = GetPortBase(myCount),
                        BaseGatewayPort = GetProxyBase(myCount)
                    }
                };
                builder.AddSiloBuilderConfigurator<TestSiloBuilderConfigurator>();
                builder.ConfigureLegacyConfiguration(legacy =>
                {
                    legacy.ClusterConfiguration.AddMemoryStorageProvider("Default");
                    legacy.ClusterConfiguration.AddMemoryStorageProvider("MemoryStore");
                    customizer?.Invoke(legacy.ClusterConfiguration);
                    if (myCount == 0)
                        gossipStabilizationTime = GetGossipStabilizationTime(legacy.ClusterConfiguration.Globals);
                });
                testCluster = builder.Build();
                testCluster.Deploy();

                Clusters[clusterId] = new ClusterInfo
                {
                    Cluster = testCluster,
                    SequenceNumber = myCount
                };
                
                WriteLog("Cluster {0} started. [{1}]", clusterId, string.Join(" ", testCluster.GetActiveSilos().Select(s => s.ToString())));
            }
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
                throw;
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
                    info.Cluster.StopAllSilos();
                });
                Clusters.Clear();
            }
        }

        #endregion

        #region client wrappers

        private readonly List<ClientWrapperBase> activeClients = new List<ClientWrapperBase>();


        // The following is a base class to use for creating client wrappers.
        // This allows us to create multiple clients that are connected to different silos.
        public class ClientWrapperBase : IDisposable
        {
            public string Name { get; private set; }

            internal IInternalClusterClient InternalClient { get; }

            public IClusterClient Client => this.InternalClient;
            private readonly Lazy<ClientConfiguration> clientConfiguration =
                new Lazy<ClientConfiguration>(
                    () => ClientConfiguration.LoadFromFile("ClientConfigurationForTesting.xml"));
            public ClientWrapperBase(string name, int gatewayport, string clusterId, Action<ClientConfiguration> configCustomizer)
            {
                this.Name = name;

                Console.WriteLine("Initializing client {0}");

                ClientConfiguration config = null;
                try
                {
                    config = this.clientConfiguration.Value;
                }
                catch (Exception) { }

                if (config == null)
                {
                    Assert.True(false, "Error loading client configuration file");
                }

                config.GatewayProvider = Orleans.Runtime.Configuration.ClientConfiguration.GatewayProviderType.Config;
                config.Gateways.Clear();
                config.Gateways.Add(new IPEndPoint(IPAddress.Loopback, gatewayport));

                configCustomizer?.Invoke(config);

                this.InternalClient = (IInternalClusterClient) new ClientBuilder()
                    .ConfigureApplicationParts(parts => parts.AddFromAppDomain().AddFromApplicationBaseDirectory())
                    .UseConfiguration(config)
                    .Build();
                this.InternalClient.Connect().Wait();
            }

            public IGrainFactory GrainFactory => this.Client;

            public void Dispose()
            {
                this.InternalClient?.Dispose();
            }
        }

        // Create a new client.
        public T NewClient<T>(
            string clusterId,
            int clientNumber,
            Func<string, int, string, Action<ClientConfiguration>, T> factory,
            Action<ClientConfiguration> customizer = null) where T : ClientWrapperBase
        {
            var ci = this.Clusters[clusterId];
            var name = string.Format("Client-{0}-{1}", clusterId, clientNumber);

            // clients are assigned to silos round-robin
            var gatewayport = ci.Silos.ElementAt(clientNumber).GatewayAddress.Endpoint.Port;

            WriteLog("Starting {0} connected to {1}", name, gatewayport);
            
            var client = factory(name, gatewayport, clusterId, customizer);

            lock (activeClients)
            {
                activeClients.Add(client);
            }

            WriteLog("Started {0} connected", name);

            return client;
        }

        public void StopAllClients()
        {
            List<ClientWrapperBase> clients;

            lock (activeClients)
            {
                clients = activeClients.ToList();
                activeClients.Clear();
            }

            Parallel.For(0, clients.Count, paralleloptions, (i) =>
            {
                try
                {
                    this.WriteLog("Stopping client {0}", i);
                    clients[i]?.Client.Close().Wait();
                }
                catch (Exception e)
                {
                    this.WriteLog("Exception caught While stopping client {0}: {1}", i, e);
                }
                finally
                {
                    clients[i]?.Dispose();
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
                    silo.AppDomainTestHook.BlockSiloCommunication(dest.SiloAddress.Endpoint, 100);
                }
        }

        public void UnblockAllClusterCommunication(string from)
        {
            foreach (var silo in Clusters[from].Silos)
            {
                WriteLog("Unblocking {0}", silo);
                silo.AppDomainTestHook.UnblockSiloCommunication();
            }
        }
  
        public void SetProtocolMessageFilterForTesting(string origincluster, Func<ILogConsistencyProtocolMessage,bool> filter)
        {
            var silos = Clusters[origincluster].Silos;
            foreach (var silo in silos)
                silo.AppDomainTestHook.ProtocolMessageFilterForTesting = filter;

        }
  
        private SiloHandle GetActiveSiloInClusterByName(string clusterId, string siloName)
        {
            if (Clusters[clusterId].Silos == null) return null;
            return Clusters[clusterId].Cluster.GetActiveSilos().FirstOrDefault(s => s.Name == siloName);
        }
    }
}