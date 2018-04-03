using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Xml;
using Orleans.Configuration;

namespace Orleans.Runtime.Configuration
{
    /// <summary>
    /// Individual node-specific silo configuration parameters.
    /// </summary>
    [Serializable]
    public class NodeConfiguration :IStatisticsConfiguration
    {
        private readonly DateTime creationTimestamp;
        private string siloName;

        /// <summary>
        /// The name of this silo.
        /// </summary>
        public string SiloName
        {
            get { return this.siloName; }
            set
            {
                this.siloName = value;
            }
        }

        /// <summary>
        /// The DNS host name of this silo.
        /// This is a true host name, no IP address. It is NOT settable, equals Dns.GetHostName().
        /// </summary>
        public string DNSHostName { get; private set; }

        /// <summary>
        /// The host name or IP address of this silo.
        /// This is a configurable IP address or Hostname.
        /// </summary>
        public string HostNameOrIPAddress { get; set; }
        private IPAddress Address { get { return ConfigUtilities.ResolveIPAddress(this.HostNameOrIPAddress, this.Subnet, this.AddressType).GetResult(); } }

        /// <summary>
        /// The port this silo uses for silo-to-silo communication.
        /// </summary>
        public int Port { get; set; }

        /// <summary>
        /// The epoch generation number for this silo.
        /// </summary>
        public int Generation { get; set; }

        /// <summary>
        /// The IPEndPoint this silo uses for silo-to-silo communication.
        /// </summary>
        public IPEndPoint Endpoint
        {
            get { return new IPEndPoint(this.Address, this.Port); }
        }
        /// <summary>
        /// The AddressFamilyof the IP address of this silo.
        /// </summary>
        public AddressFamily AddressType { get; set; }
        /// <summary>
        /// The IPEndPoint this silo uses for (gateway) silo-to-client communication.
        /// </summary>
        public IPEndPoint ProxyGatewayEndpoint { get; set; }

        public byte[] Subnet { get; set; } // from global

        /// <summary>
        /// Whether this is a primary silo (applies for dev settings only).
        /// </summary>
        public bool IsPrimaryNode { get; set; }
        /// <summary>
        /// Whether this is one of the seed silos (applies for dev settings only).
        /// </summary>
        public bool IsSeedNode { get; set; }
        /// <summary>
        /// Whether this is silo is a proxying gateway silo.
        /// </summary>
        public bool IsGatewayNode { get { return this.ProxyGatewayEndpoint != null; } }

        /// <summary>
        /// The MaxActiveThreads attribute specifies the maximum number of simultaneous active threads the scheduler will allow.
        /// Generally this number should be roughly equal to the number of cores on the node.
        /// Using a value of 0 will look at System.Environment.ProcessorCount to decide the number instead, which is only valid when set from xml config
        /// </summary>
        public int MaxActiveThreads { get; set; }

        /// <summary>
        /// The DelayWarningThreshold attribute specifies the work item queuing delay threshold, at which a warning log message is written.
        /// That is, if the delay between enqueuing the work item and executing the work item is greater than DelayWarningThreshold, a warning log is written.
        /// The default value is 10 seconds.
        /// </summary>
        public TimeSpan DelayWarningThreshold { get; set; }

        /// <summary>
        /// ActivationSchedulingQuantum is a soft time limit on the duration of activation macro-turn (a number of micro-turns). 
        /// If a activation was running its micro-turns longer than this, we will give up the thread.
        /// If this is set to zero or a negative number, then the full work queue is drained (MaxWorkItemsPerTurn allowing).
        /// </summary>
        public TimeSpan ActivationSchedulingQuantum { get; set; }

        /// <summary>
        /// TurnWarningLengthThreshold is a soft time limit to generate trace warning when the micro-turn executes longer then this period in CPU. 
        /// </summary>
        public TimeSpan TurnWarningLengthThreshold { get; set; }

        internal bool EnableWorkerThreadInjection { get; set; }

        /// <summary>
        /// The LoadShedding element specifies the gateway load shedding configuration for the node.
        /// If it does not appear, gateway load shedding is disabled.
        /// </summary>
        public bool LoadSheddingEnabled { get; set; }

        /// <summary>
        /// The LoadLimit attribute specifies the system load, in CPU%, at which load begins to be shed.
        /// Note that this value is in %, so valid values range from 1 to 100, and a reasonable value is
        /// typically between 80 and 95.
        /// This value is ignored if load shedding is disabled, which is the default.
        /// If load shedding is enabled and this attribute does not appear, then the default limit is 95%.
        /// </summary>
        public int LoadSheddingLimit { get; set; }

        /// <summary>
        /// The values for various silo limits.
        /// </summary>
        public LimitManager LimitManager { get; private set; }

        /// <summary>
        ///  Whether Trace.CorrelationManager.ActivityId settings should be propagated into grain calls.
        /// </summary>
        public bool PropagateActivityId { get; set; }

        /// <summary>
        /// Specifies the name of the Startup class in the configuration file.
        /// </summary>
        public string StartupTypeName { get; set; }

        [Obsolete("Statistics publishers are no longer supported.")]
        public string StatisticsProviderName { get; set; }

        /// <summary>
        /// The MetricsTableWriteInterval attribute specifies the frequency of updating the metrics in Azure table.
        ///  The default is 30 seconds.
        /// </summary>
        public TimeSpan StatisticsMetricsTableWriteInterval { get; set; }
        /// <summary>
        /// The PerfCounterWriteInterval attribute specifies the frequency of updating the windows performance counters.
        /// The default is 30 seconds.
        /// </summary>
        public TimeSpan StatisticsPerfCountersWriteInterval { get; set; }
        /// <summary>
        /// The LogWriteInterval attribute specifies the frequency of updating the statistics in the log file.
        /// The default is 5 minutes.
        /// </summary>
        public TimeSpan StatisticsLogWriteInterval { get; set; }

        /// <summary>
        /// The WriteLogStatisticsToTable attribute specifies whether log statistics should also be written into a separate, special Azure table.
        ///  The default is yes.
        /// </summary>
        [Obsolete("Statistics table is no longer supported.")]
        public bool StatisticsWriteLogStatisticsToTable { get; set; }
        /// <summary>
        /// </summary>
        public StatisticsLevel StatisticsCollectionLevel { get; set; }

        /// <summary>
        /// </summary>
        public int MinDotNetThreadPoolSize { get; set; }
        /// <summary>
        /// </summary>
        public bool Expect100Continue { get; set; }
        /// <summary>
        /// </summary>
        public int DefaultConnectionLimit { get; set; }
        /// <summary>
        /// </summary>
        public bool UseNagleAlgorithm { get; set; }

        public TelemetryConfiguration TelemetryConfiguration { get; } = new TelemetryConfiguration();

        public Dictionary<string, SearchOption> AdditionalAssemblyDirectories { get; set; }

        public List<string> ExcludedGrainTypes { get; set; }

        public string SiloShutdownEventName { get; set; }

        internal const string DEFAULT_NODE_NAME = "default";
		
		private static int DEFAULT_MAX_ACTIVE_THREADS = Math.Max(4, Environment.ProcessorCount);
		private static TimeSpan DEFAULT_DELAY_WARNING_THRESHOLD = TimeSpan.FromMilliseconds(10000);
		private static TimeSpan DEFAULT_ACTIVATION_SCHEDULING_QUANTUM = TimeSpan.FromMilliseconds(100);
		private static TimeSpan DEFAULT_TURN_WARNING_THRESHOLD = TimeSpan.FromMilliseconds(200);
		private static TimeSpan DEFAULT_METRICS_TABLE_WRITE_PERIOD = TimeSpan.FromSeconds(30);
		private static TimeSpan SILO_DEFAULT_PERF_COUNTERS_WRITE_PERIOD = TimeSpan.FromSeconds(30);
		private static TimeSpan DEFAULT_LOG_WRITE_PERIOD = TimeSpan.FromMinutes(5);

        private const bool DEFAULT_ENABLE_WORKER_THREAD_INJECTION = false;
		private const StatisticsLevel DEFAULT_COLLECTION_LEVEL = StatisticsLevel.Info;

        public NodeConfiguration()
        {
            this.creationTimestamp = DateTime.UtcNow;

            this.SiloName = "";
            this.HostNameOrIPAddress = "";
            this.DNSHostName = Dns.GetHostName();
            this.Port = 0;
            this.Generation = 0;
            this.AddressType = AddressFamily.InterNetwork;
            this.ProxyGatewayEndpoint = null;

            this.MaxActiveThreads = DEFAULT_MAX_ACTIVE_THREADS;
            this.DelayWarningThreshold = DEFAULT_DELAY_WARNING_THRESHOLD;
            this.ActivationSchedulingQuantum = DEFAULT_ACTIVATION_SCHEDULING_QUANTUM;
            this.TurnWarningLengthThreshold = DEFAULT_TURN_WARNING_THRESHOLD;
            this.EnableWorkerThreadInjection = DEFAULT_ENABLE_WORKER_THREAD_INJECTION;

            this.LoadSheddingEnabled = false;
            this.LoadSheddingLimit = LoadSheddingOptions.DEFAULT_LOAD_SHEDDING_LIMIT;

            this.PropagateActivityId = MessagingOptions.DEFAULT_PROPAGATE_E2E_ACTIVITY_ID;

            this.StatisticsMetricsTableWriteInterval = DEFAULT_METRICS_TABLE_WRITE_PERIOD;
            this.StatisticsPerfCountersWriteInterval = SILO_DEFAULT_PERF_COUNTERS_WRITE_PERIOD;
            this.StatisticsLogWriteInterval = DEFAULT_LOG_WRITE_PERIOD;
            this.StatisticsCollectionLevel = DEFAULT_COLLECTION_LEVEL;

            this.LimitManager = new LimitManager();

            this.MinDotNetThreadPoolSize = PerformanceTuningOptions.DEFAULT_MIN_DOT_NET_THREAD_POOL_SIZE;

            // .NET ServicePointManager settings / optimizations
            this.Expect100Continue = false;
            this.DefaultConnectionLimit = PerformanceTuningOptions.DEFAULT_MIN_DOT_NET_CONNECTION_LIMIT;
            this.UseNagleAlgorithm = false;

            this.AdditionalAssemblyDirectories = new Dictionary<string, SearchOption>();
            this.ExcludedGrainTypes = new List<string>();
        }

        public NodeConfiguration(NodeConfiguration other)
        {
            this.creationTimestamp = other.creationTimestamp;

            this.SiloName = other.SiloName;
            this.HostNameOrIPAddress = other.HostNameOrIPAddress;
            this.DNSHostName = other.DNSHostName;
            this.Port = other.Port;
            this.Generation = other.Generation;
            this.AddressType = other.AddressType;
            this.ProxyGatewayEndpoint = other.ProxyGatewayEndpoint;

            this.MaxActiveThreads = other.MaxActiveThreads;
            this.DelayWarningThreshold = other.DelayWarningThreshold;
            this.ActivationSchedulingQuantum = other.ActivationSchedulingQuantum;
            this.TurnWarningLengthThreshold = other.TurnWarningLengthThreshold;
            this.EnableWorkerThreadInjection = other.EnableWorkerThreadInjection;

            this.LoadSheddingEnabled = other.LoadSheddingEnabled;
            this.LoadSheddingLimit = other.LoadSheddingLimit;

            this.PropagateActivityId = other.PropagateActivityId;

#pragma warning disable CS0618 // Type or member is obsolete
            this.StatisticsProviderName = other.StatisticsProviderName;
#pragma warning restore CS0618 // Type or member is obsolete
            this.StatisticsMetricsTableWriteInterval = other.StatisticsMetricsTableWriteInterval;
            this.StatisticsPerfCountersWriteInterval = other.StatisticsPerfCountersWriteInterval;
            this.StatisticsLogWriteInterval = other.StatisticsLogWriteInterval;
#pragma warning disable CS0618 // Type or member is obsolete
            this.StatisticsWriteLogStatisticsToTable = other.StatisticsWriteLogStatisticsToTable;
#pragma warning restore CS0618 // Type or member is obsolete
            this.StatisticsCollectionLevel = other.StatisticsCollectionLevel;

            this.LimitManager = new LimitManager(other.LimitManager); // Shallow copy

            this.Subnet = other.Subnet;

            this.MinDotNetThreadPoolSize = other.MinDotNetThreadPoolSize;
            this.Expect100Continue = other.Expect100Continue;
            this.DefaultConnectionLimit = other.DefaultConnectionLimit;
            this.UseNagleAlgorithm = other.UseNagleAlgorithm;

            this.StartupTypeName = other.StartupTypeName;
            this.AdditionalAssemblyDirectories = new Dictionary<string, SearchOption>(other.AdditionalAssemblyDirectories);
            this.ExcludedGrainTypes = other.ExcludedGrainTypes.ToList();
            this.TelemetryConfiguration = other.TelemetryConfiguration.Clone();
        }

        public override string ToString()
        {
            var sb = new StringBuilder();

            sb.Append("   Silo Name: ").AppendLine(this.SiloName);
            sb.Append("   Generation: ").Append(this.Generation).AppendLine();
            sb.Append("   Host Name or IP Address: ").AppendLine(this.HostNameOrIPAddress);
            sb.Append("   DNS Host Name: ").AppendLine(this.DNSHostName);
            sb.Append("   Port: ").Append(this.Port).AppendLine();
            sb.Append("   Subnet: ").Append(this.Subnet == null ? "" : this.Subnet.ToStrings(x => x.ToString(), ".")).AppendLine();
            sb.Append("   Preferred Address Family: ").Append(this.AddressType).AppendLine();
            if (this.IsGatewayNode)
            {
                sb.Append("   Proxy Gateway: ").Append(this.ProxyGatewayEndpoint.ToString()).AppendLine();
            }
            else
            {
                sb.Append("   IsGatewayNode: ").Append(this.IsGatewayNode).AppendLine();
            }
            sb.Append("   IsPrimaryNode: ").Append(this.IsPrimaryNode).AppendLine();
            sb.Append("   Scheduler: ").AppendLine();
            sb.Append("      ").Append("   Max Active Threads: ").Append(this.MaxActiveThreads).AppendLine();
            sb.Append("      ").Append("   Processor Count: ").Append(System.Environment.ProcessorCount).AppendLine();
            sb.Append("      ").Append("   Delay Warning Threshold: ").Append(this.DelayWarningThreshold).AppendLine();
            sb.Append("      ").Append("   Activation Scheduling Quantum: ").Append(this.ActivationSchedulingQuantum).AppendLine();
            sb.Append("      ").Append("   Turn Warning Length Threshold: ").Append(this.TurnWarningLengthThreshold).AppendLine();
            sb.Append("      ").Append("   Inject More Worker Threads: ").Append(this.EnableWorkerThreadInjection).AppendLine();
            sb.Append("      ").Append("   MinDotNetThreadPoolSize: ").Append(this.MinDotNetThreadPoolSize).AppendLine();

            int workerThreads;
            int completionPortThreads;
            ThreadPool.GetMinThreads(out workerThreads, out completionPortThreads);
            sb.Append("      ").AppendFormat("   .NET thread pool sizes - Min: Worker Threads={0} Completion Port Threads={1}", workerThreads, completionPortThreads).AppendLine();
            ThreadPool.GetMaxThreads(out workerThreads, out completionPortThreads);
            sb.Append("      ").AppendFormat("   .NET thread pool sizes - Max: Worker Threads={0} Completion Port Threads={1}", workerThreads, completionPortThreads).AppendLine();

            sb.Append("      ").AppendFormat("   .NET ServicePointManager - DefaultConnectionLimit={0} Expect100Continue={1} UseNagleAlgorithm={2}", this.DefaultConnectionLimit, this.Expect100Continue, this.UseNagleAlgorithm).AppendLine();
            sb.Append("   Load Shedding Enabled: ").Append(this.LoadSheddingEnabled).AppendLine();
            sb.Append("   Load Shedding Limit: ").Append(this.LoadSheddingLimit).AppendLine();
            sb.Append("   SiloShutdownEventName: ").Append(this.SiloShutdownEventName).AppendLine();
            sb.Append("   Debug: ").AppendLine();
            sb.Append(ConfigUtilities.IStatisticsConfigurationToString(this));
            sb.Append(this.LimitManager);
            return sb.ToString();
        }

        internal void InitNodeSettingsFromGlobals(ClusterConfiguration clusterConfiguration)
        {
            this.IsPrimaryNode = this.Endpoint.Equals(clusterConfiguration.PrimaryNode);
            this.IsSeedNode = clusterConfiguration.Globals.SeedNodes.Contains(this.Endpoint);
        }

        internal void Load(XmlElement root)
        {
            this.SiloName = root.LocalName.Equals("Override") ? root.GetAttribute("Node") : DEFAULT_NODE_NAME;

            foreach (XmlNode c in root.ChildNodes)
            {
                var child = c as XmlElement;

                if (child == null) continue; // Skip comment lines

                switch (child.LocalName)
                {
                    case "Networking":
                        if (child.HasAttribute("Address"))
                        {
                            this.HostNameOrIPAddress = child.GetAttribute("Address");
                        }
                        if (child.HasAttribute("Port"))
                        {
                            this.Port = ConfigUtilities.ParseInt(child.GetAttribute("Port"),
                                "Non-numeric Port attribute value on Networking element for " + this.SiloName);
                        }
                        if (child.HasAttribute("PreferredFamily"))
                        {
                            this.AddressType = ConfigUtilities.ParseEnum<AddressFamily>(child.GetAttribute("PreferredFamily"),
                                "Invalid preferred address family on Networking node. Valid choices are 'InterNetwork' and 'InterNetworkV6'");
                        }
                        break;
                    case "ProxyingGateway":
                        this.ProxyGatewayEndpoint = ConfigUtilities.ParseIPEndPoint(child, this.Subnet).GetResult();
                        break;
                    case "Scheduler":
                        if (child.HasAttribute("MaxActiveThreads"))
                        {
                            this.MaxActiveThreads = ConfigUtilities.ParseInt(child.GetAttribute("MaxActiveThreads"),
                                "Non-numeric MaxActiveThreads attribute value on Scheduler element for " + this.SiloName);
                            if (this.MaxActiveThreads < 1)
                            {
                                this.MaxActiveThreads = Math.Max(4, System.Environment.ProcessorCount);
                            }
                        }
                        if (child.HasAttribute("DelayWarningThreshold"))
                        {
                            this.DelayWarningThreshold = ConfigUtilities.ParseTimeSpan(child.GetAttribute("DelayWarningThreshold"),
                                "Non-numeric DelayWarningThreshold attribute value on Scheduler element for " + this.SiloName);
                        }
                        if (child.HasAttribute("ActivationSchedulingQuantum"))
                        {
                            this.ActivationSchedulingQuantum = ConfigUtilities.ParseTimeSpan(child.GetAttribute("ActivationSchedulingQuantum"),
                                "Non-numeric ActivationSchedulingQuantum attribute value on Scheduler element for " + this.SiloName);
                        }
                        if (child.HasAttribute("TurnWarningLengthThreshold"))
                        {
                            this.TurnWarningLengthThreshold = ConfigUtilities.ParseTimeSpan(child.GetAttribute("TurnWarningLengthThreshold"),
                                "Non-numeric TurnWarningLengthThreshold attribute value on Scheduler element for " + this.SiloName);
                        }
                        if (child.HasAttribute("MinDotNetThreadPoolSize"))
                        {
                            this.MinDotNetThreadPoolSize = ConfigUtilities.ParseInt(child.GetAttribute("MinDotNetThreadPoolSize"),
                                "Invalid ParseInt MinDotNetThreadPoolSize value on Scheduler element for " + this.SiloName);
                        }
                        if (child.HasAttribute("Expect100Continue"))
                        {
                            this.Expect100Continue = ConfigUtilities.ParseBool(child.GetAttribute("Expect100Continue"),
                                "Invalid ParseBool Expect100Continue value on Scheduler element for " + this.SiloName);
                        }
                        if (child.HasAttribute("DefaultConnectionLimit"))
                        {
                            this.DefaultConnectionLimit = ConfigUtilities.ParseInt(child.GetAttribute("DefaultConnectionLimit"),
                                "Invalid ParseInt DefaultConnectionLimit value on Scheduler element for " + this.SiloName);
                        }
                        if (child.HasAttribute("UseNagleAlgorithm "))
                        {
                            this.UseNagleAlgorithm = ConfigUtilities.ParseBool(child.GetAttribute("UseNagleAlgorithm "),
                                "Invalid ParseBool UseNagleAlgorithm value on Scheduler element for " + this.SiloName);
                        }
                        break;
                    case "LoadShedding":
                        if (child.HasAttribute("Enabled"))
                        {
                            this.LoadSheddingEnabled = ConfigUtilities.ParseBool(child.GetAttribute("Enabled"),
                                "Invalid boolean value for Enabled attribute on LoadShedding attribute for " + this.SiloName);
                        }
                        if (child.HasAttribute("LoadLimit"))
                        {
                            this.LoadSheddingLimit = ConfigUtilities.ParseInt(child.GetAttribute("LoadLimit"),
                                "Invalid integer value for LoadLimit attribute on LoadShedding attribute for " + this.SiloName);
                            if (this.LoadSheddingLimit < 0)
                            {
                                this.LoadSheddingLimit = 0;
                            }
                            if (this.LoadSheddingLimit > 100)
                            {
                                this.LoadSheddingLimit = 100;
                            }
                        }
                        break;
                    case "Tracing":
                        if (ConfigUtilities.TryParsePropagateActivityId(child, this.siloName, out var propagateActivityId))
                            this.PropagateActivityId = propagateActivityId;
                        break;
                    case "Statistics":
                        ConfigUtilities.ParseStatistics(this, child, this.SiloName);
                        break;
                    case "Limits":
                        ConfigUtilities.ParseLimitValues(this.LimitManager, child, this.SiloName);
                        break;
                    case "Startup":
                        if (child.HasAttribute("Type"))
                        {
                            this.StartupTypeName = child.GetAttribute("Type");
                        }
                        break;
                    case "Telemetry":
                        ConfigUtilities.ParseTelemetry(child, this.TelemetryConfiguration);
                        break;
                    case "AdditionalAssemblyDirectories":
                        ConfigUtilities.ParseAdditionalAssemblyDirectories(this.AdditionalAssemblyDirectories, child);
                        break;
                }
            }
        }
    }
}
