using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Orleans.TestFramework
{
    public class MockStreamProviderParameters
    {
        public string StreamProvider { get; set; }
        public int TotalQueueCount { get; set; }
        public int NumStreamsPerQueue { get; set; }
        public string MessageProducer { get; set; }
        public int ActivationTaskDelay { get; set; }
        public int ActivationBusyWait { get; set; }
        public int AdditionalSubscribersCount { get; set; }
        public int EventTaskDelay { get; set; }
        public int EventBusyWait { get; set; }
        public int SiloStabilizationTime { get; set; }
        public int RampUpStagger { get; set; }
        public int SubscriptionLength { get; set; }
        public int StreamEventsPerSecond { get; set; }
        public int TargetBatchesSentPerSecond { get; set; }
        public int MaxBatchesPerRequest { get; set; }
        public int MaxEventsPerBatch { get; set; }
        public int EventSize { get; set; }
        public int CacheSizeKb { get; set; }

        //public OLD_MockStreamProviderParameters()
        //{
        //    // The target is 1024 Event Hub partitions (queues) on 64 silos:
        //    // 4 pulling agents per silo 
        //    // 4 queues per agent
        //    // 100 concurrent streams per queue
        //    // 100 events per second per queue, so 1 event per second per stream 
        //    // Per silo: 
        //    //      1600 concurrent streams
        //    //      1600 events per second (across all streams)
        //    // Based on these numbers need 64 silos.
        //    // We will scale it down proportionally and test with 128 queues on 8 silos.
        //    StreamProvider = "OldMockStreamProvider";
        //    TotalQueueCount = 128;
        //    NumStreamsPerQueue = 100;
        //    MessageProducer = "ReentrantImplicitConsumer";
        //    SiloStabilizationTime = (int)TimeSpan.FromMinutes(1).TotalMilliseconds;
        //    TargetBatchesSentPerSecond = 100;
        //    MaxBatchesPerRequest = 1;
        //    MaxEventsPerBatch = 1;
        //    CacheSizeKb = 4;
        //}

        public MockStreamProviderParameters()
        {
            // The target is 1024 Event Hub partitions (queues) on 64 silos:
            // 4 pulling agents per silo 
            // 4 queues per agent
            // 100 concurrent streams per queue
            // so 1 event per second per stream 
            // Per silo: 
            //      1600 concurrent streams
            //      1600 events per second (across all streams)
            // Based on these numbers need 64 silos.
            // We will scale it down proportionally and test with 128 queues on 8 silos.
            StreamProvider = "MockStreamProvider";
            TotalQueueCount = 128;
            NumStreamsPerQueue = 100;
            MessageProducer = "ImplicitConsumer"; // "ReentrantImplicitConsumer";
            SiloStabilizationTime = (int)TimeSpan.FromMinutes(1).TotalMilliseconds;
            TargetBatchesSentPerSecond = 100;
            MaxBatchesPerRequest = 1;
            MaxEventsPerBatch = 1;
            CacheSizeKb = 512;

            ActivationTaskDelay = (int) TimeSpan.FromMilliseconds(1).TotalMilliseconds; //(int)TimeSpan.FromMilliseconds(200).TotalMilliseconds;
            EventTaskDelay = (int) TimeSpan.FromMilliseconds(1).TotalMilliseconds; //(int)TimeSpan.FromMilliseconds(20).TotalMilliseconds;
            AdditionalSubscribersCount = 0;//8;
            RampUpStagger = (int)(TimeSpan.FromMinutes(7).TotalMilliseconds / NumStreamsPerQueue);
            SubscriptionLength = (int)TimeSpan.FromMinutes(7).TotalMilliseconds;
            StreamEventsPerSecond = 1;
            EventSize = 50;
        }
    }

    public class PlacementStrategyParameters
    {
        public string DefaultPlacementStrategy { get; set; }
        public TimeSpan DeploymentLoadPublisherRefreshTime { get; set; }
        public int ActivationCountBasedPlacementChooseOutOf { get; set; }
    }

    public class DeploymentConfig
    {
        public string Name { get; set; }
        public Guid ServiceId { get; set; }
        public string DeploymentId { get; set; }
        public string SdkDropPath { get; set; }
        public string TestLogs { get; set; }
        public string ClientAppPath { get; set; }
        public string Subnet { get; set; }
        public int StartPort { get; set; }
        public int GatewayPort { get; set; }
        public string Primary { get; set; }
        public List<string> ServerMachines = new List<string>();
        public List<string> ClientMachines = new List<string>();
        public string ServerConfigTemplate { get; set; }
        public string ClientConfigTemplate { get; set; }
        public bool AllowMachineReuse { get; set; }
        public SiloOptions SiloOptions { get; set; }
        public Dictionary<string, string> Applications = new Dictionary<string, string>();
        public Dictionary<string, string> Clients = new Dictionary<string, string>();
        /// <summary>
        /// Sets the client app for the test
        /// </summary>
        /// <param name="clientName"></param>
        public void SelectClient(string clientName)
        {
            string client = null;
            if (Clients.TryGetValue(clientName, out client))
            {
                ClientAppPath = client;
            }
            else
            {
                throw new KeyNotFoundException("Could not find client " + clientName + " in the list of clients. List of clients is: " + Utils.DictionaryToString(Clients));
            }
        }

        public void SetServerMachines(List<string> machines)
        {
            lock (this)
            {        
                ServerMachines.AddRange(machines);
            }
        }

        public void SetClientMachines(List<string> machines)
        {
            lock (this)
            {
                ClientMachines.AddRange(machines);
            }
        }

        /// <summary>
        /// Set client metric to range of metric
        /// </summary>
        /// <param name="prefix">prefix used</param>
        /// <param name="start">start machine number</param>
        /// <param name="end">end machine number</param>
        public static List<string> GetMachines(string prefix, int start, int end, int[] skips)
        {
            List<string> machines = new List<string>();
            for (int i = start; i <= end; i++)
            {
                if (skips == null || !skips.Contains(i))
                {
                    string name = string.Format("{0}{1:D2}", prefix, i);
                    DeploymentManager.VerifyDeploymentMachine(name);
                    machines.Add(name);
                }
            }
            return machines;
        }

        /// <summary>
        /// Index used to keep track of metric.
        /// </summary>
        private  int serverMachineIndex = 0;

        /// <summary>
        /// Index used to keep track of metric.
        /// </summary>
        private  int clientMachineIndex = 0;

        /// <summary>
        /// Map to manage machine assignments
        /// </summary>
        private Dictionary<string, string> siloNamesToMachineMap = new Dictionary<string, string>();
        /// <summary>
        /// Map to manage machine assignments
        /// </summary>
        private Dictionary<string, string> clientNamesToMachineMap = new Dictionary<string, string>();

        /// <summary>
        /// Preassigns metric to the silonames
        /// </summary>
        /// <param name="count">number of silos</param>
        /// <returns>Name of generated silo names</returns>
        public List<string> PreAssignSiloMachines(int count)
        {
            lock (this)
            {
                List<string> retValue = new List<string>();
                for (int i = 0; i < count; i++)
                {
                    string name = string.Format("Silo{0}", i.ToString("D2"));
                    string machine = GetAssignedSiloMachine(name);
                    retValue.Add(name);
                }
                return retValue;
            }
        }
        /// <summary>
        /// Gets the name of the machine on which silo will run.
        /// </summary>
        /// <param name="siloName">Name of the silo.</param>
        /// <returns>Name of the machine.</returns>
        public string GetAssignedSiloMachine(string siloName)
        {
            lock (this)
            {
                // make first machine primary
                if (siloNamesToMachineMap.Count == 0)
                {
                    Primary = siloName;
                }
                // assign machine if not already assigned.
                if (!siloNamesToMachineMap.ContainsKey(siloName))
                {
                    if (!AllowMachineReuse && (serverMachineIndex >= ServerMachines.Count))
                    {
                        Assert.Fail("More silos specified than available machines");
                    }
                    string siloToUse = ServerMachines[serverMachineIndex % ServerMachines.Count];
                    serverMachineIndex++;
                    siloNamesToMachineMap.Add(siloName, siloToUse);
                }
                return siloNamesToMachineMap[siloName];
            }
        }

        public List<string> GetAssignedSiloMachines()
        {
            lock (this)
            {
                return new List<string>(siloNamesToMachineMap.Keys);
            }
        }
        /// <summary>
        /// Gets the name of the machine on which client will run.
        /// </summary>
        /// <param name="clientName">Name of the client.</param>
        /// <returns>Name of the machine.</returns>
        public string GetAssignedClientMachine(string clientName)
        {
            lock (this)
            {
                // assign machine if not already assigned.
                if (!clientNamesToMachineMap.ContainsKey(clientName))
                {
                    if (!AllowMachineReuse && (clientMachineIndex >= ClientMachines.Count))
                    {
                        Assert.Fail("More clients specified than available machines");
                    }
                    clientNamesToMachineMap.Add(clientName, ClientMachines[clientMachineIndex % ClientMachines.Count]);
                    clientMachineIndex++;
                }
                return clientNamesToMachineMap[clientName];
            }
        }
    }
}
