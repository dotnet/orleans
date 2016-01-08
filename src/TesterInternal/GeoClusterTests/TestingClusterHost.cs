using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Orleans.TestingHost;
using System.Reflection;
using System.Globalization;
using UnitTests.Tester;
using Orleans.Runtime.Configuration;
using System.Net;
using System.Net.Sockets;
using Orleans;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tests.GeoClusterTests
{
    /// <summary>
    /// A utility class for tests that include multiple clusters.
    /// It calls static methods on TestingSiloHost for starting and stopping silos.
    /// </summary>
    public class TestingClusterHost   
    {
        protected readonly Dictionary<string, ClusterInfo> Clusters;

        public TestingClusterHost() : base()
        {
            Clusters = new Dictionary<string, ClusterInfo>();

            UnitTestSiloHost.CheckForAzureStorage();
        }

        protected struct ClusterInfo
        {
            public List<SiloHandle> Silos;  // currently active silos
            public int SequenceNumber; // we number created clusters in order of creation
        }

    

        public static void WriteLog(string format, params object[] args)
        {
            Console.WriteLine(format, args);
        }


        #region Default Cluster and Client Configuration

        private static int GetPortBase(int clusternumber)
        {
            return 21000 + (clusternumber + 1) * 100;
        }
        private static int GetProxyBase(int clusternumber)
        {
            return 22000 + (clusternumber + 2) * 100;
        }
        private static int DetermineGatewayPort(int clusternumber, int clientnumber)
        {
            return GetProxyBase(clusternumber) + clientnumber % 3;
        }
     
        #endregion


        #region Cluster Creation


        public void NewCluster(string clusterid, int numSilos, Action<ClusterConfiguration> customizer = null)
        {
            
            lock (Clusters)
            {
                WriteLog("Starting Cluster {0}...", clusterid);

                var mycount = Clusters.Count;

                var silohandles = new SiloHandle[numSilos];

                var options = new TestingSiloOptions
                {
                    StartClient = false,
                    AdjustConfig = customizer,
                    BasePort = GetPortBase(mycount),
                    ProxyBasePort = GetProxyBase(mycount)
                };
                silohandles[0] = TestingSiloHost.StartOrleansSilo(null, Silo.SiloType.Primary, options, 0);

                Parallel.For(1, numSilos, i =>
                {
                    silohandles[i] = TestingSiloHost.StartOrleansSilo(null, Silo.SiloType.Secondary, options, i);
                });

                Clusters[clusterid] = new ClusterInfo
                {
                    Silos = silohandles.ToList(),
                    SequenceNumber = mycount
                };

                WriteLog("Cluster {0} started.", clusterid);
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

            var silo = TestingSiloHost.StartOrleansSilo(null, Silo.SiloType.Secondary, options, clusterinfo.Silos.Count);
        }

      

        public void StopAllClientsAndClusters()
        {
            WriteLog("Stopping All Clients and Clusters...");
            StopAllClients();
            StopAllClusters();
            WriteLog("All Clients and Clusters Are Stopped.");
        }

        public void StopAllClusters()
        {
            lock (Clusters)
            {
                Parallel.ForEach(Clusters.Keys, key =>
                {
                    var info = Clusters[key];
                    Parallel.For(1, info.Silos.Count, i => TestingSiloHost.StopSilo(info.Silos[i]));
                    TestingSiloHost.StopSilo(info.Silos[0]);
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

            public ClientWrapperBase(string name, int gatewayport)
            {
                this.Name = name;

                Console.WriteLine("Initializing client {0} in AppDomain {1}", name, AppDomain.CurrentDomain.FriendlyName);

                ClientConfiguration config = null;
                try
                {
                    config = ClientConfiguration.LoadFromFile("ClientConfigurationForTesting.xml");
                }
                catch (Exception) { }

                if (config == null)
                {
                    Assert.Fail("Error loading client configuration file");
                }
                config.GatewayProvider = ClientConfiguration.GatewayProviderType.Config;
                config.Gateways.Clear();
                config.Gateways.Add(new IPEndPoint(IPAddress.Loopback, gatewayport));

                GrainClient.Initialize(config);
            }
            
        }

        // Create a client, loaded in a new app domain.
        public T NewClient<T>(string ClusterId, int ClientNumber) where T: ClientWrapperBase
        {
            var ci = Clusters[ClusterId];
            var name = string.Format("Client-{0}-{1}", ClusterId, ClientNumber);
            var gatewayport = DetermineGatewayPort(ci.SequenceNumber, ClientNumber);
       
            var clientArgs = new object[] { name, gatewayport };
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

            return client;
        }

        public void StopAllClients()
        {
            lock (activeClients)
            {
                foreach (var client in activeClients)
                {
                    try
                    {
                        AppDomain.Unload(client);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                        throw;
                    }
                }
                activeClients.Clear();
            }
        }

        #endregion

         


        public void BlockAllClusterCommunication(string from, string to)
        {
            foreach (var silo in Clusters[from].Silos)
                foreach (var dest in Clusters[to].Silos)
                    silo.Silo.TestHook.BlockSiloCommunication(dest.Endpoint, 100);
        }

        public void UnblockAllClusterCommunication(string from)
        {
            foreach (var silo in Clusters[from].Silos)
                    silo.Silo.TestHook.UnblockSiloCommunication();
        }

  
        private SiloHandle GetActiveSiloInClusterByName(string clusterId, string siloName)
        {
            if (Clusters[clusterId].Silos == null) return null;
            return Clusters[clusterId].Silos.Find(s => s.Name == siloName);
        }
        
    }
}