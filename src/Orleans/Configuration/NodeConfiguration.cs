using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Xml;

namespace Orleans.Runtime.Configuration
{
    /// <summary>
    /// Individual node-specific silo configuration parameters.
    /// </summary>
    [Serializable]
    public class NodeConfiguration : ITraceConfiguration, IStatisticsConfiguration
    {
        private readonly DateTime creationTimestamp;
        private string siloName;

        /// <summary>
        /// The name of this silo.
        /// </summary>
        public string SiloName
        {
            get { return siloName; }
            set
            {
                siloName = value;
                ConfigUtilities.SetTraceFileName(this, siloName, creationTimestamp);
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
        private IPAddress Address { get { return ClusterConfiguration.ResolveIPAddress(HostNameOrIPAddress, Subnet, AddressType).GetResult(); } }

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
            get { return new IPEndPoint(Address, Port); }
        }
        /// <summary>
        /// The AddressFamilyof the IP address of this silo.
        /// </summary>
        public AddressFamily AddressType { get; set; }
        /// <summary>
        /// The IPEndPoint this silo uses for (gateway) silo-to-client communication.
        /// </summary>
        public IPEndPoint ProxyGatewayEndpoint { get; set; }

        internal byte[] Subnet { get; set; } // from global

        /// <summary>
        /// Whether this is a primary silo (applies for dev settings only).
        /// </summary>
        public bool IsPrimaryNode { get; internal set; }
        /// <summary>
        /// Whether this is one of the seed silos (applies for dev settings only).
        /// </summary>
        public bool IsSeedNode { get; internal set; }
        /// <summary>
        /// Whether this is silo is a proxying gateway silo.
        /// </summary>
        public bool IsGatewayNode { get { return ProxyGatewayEndpoint != null; } }

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

        private string traceFilePattern;
        /// <summary>
        /// </summary>
        public Severity DefaultTraceLevel { get; set; }
        /// <summary>
        /// </summary>
        public IList<Tuple<string, Severity>> TraceLevelOverrides { get; private set; }
        /// <summary>
        /// </summary>
        public bool TraceToConsole { get; set; }
        /// <summary>
        /// </summary>
        public string TraceFilePattern
        {
            get { return traceFilePattern; }
            set
            {
                traceFilePattern = value;
                ConfigUtilities.SetTraceFileName(this, siloName, creationTimestamp);
            }
        }
        /// <summary>
        /// </summary>
        public string TraceFileName { get; set; }
        /// <summary>
        /// </summary>
        public int LargeMessageWarningThreshold { get; set; }
        /// <summary>
        /// </summary>
        public bool PropagateActivityId { get; set; }
        /// <summary>
        /// </summary>
        public int BulkMessageLimit { get; set; }

        /// <summary>
        /// Specifies the name of the Startup class in the configuration file.
        /// </summary>
        public string StartupTypeName { get; set; }

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

        public Dictionary<string, SearchOption> AdditionalAssemblyDirectories { get; set; }

        public List<string> ExcludedGrainTypes { get; set; }

        public string SiloShutdownEventName { get; set; }

        internal const string DEFAULT_NODE_NAME = "default";
        private static readonly TimeSpan DEFAULT_STATS_METRICS_TABLE_WRITE_PERIOD = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan DEFAULT_STATS_PERF_COUNTERS_WRITE_PERIOD = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan DEFAULT_STATS_LOG_WRITE_PERIOD = TimeSpan.FromMinutes(5);
        internal static readonly StatisticsLevel DEFAULT_STATS_COLLECTION_LEVEL = StatisticsLevel.Info;
        private static readonly int DEFAULT_MAX_ACTIVE_THREADS = Math.Max(4, System.Environment.ProcessorCount);
        private const int DEFAULT_MIN_DOT_NET_THREAD_POOL_SIZE = 200;
        private static readonly int DEFAULT_MIN_DOT_NET_CONNECTION_LIMIT = DEFAULT_MIN_DOT_NET_THREAD_POOL_SIZE;
        private static readonly TimeSpan DEFAULT_ACTIVATION_SCHEDULING_QUANTUM = TimeSpan.FromMilliseconds(100);
        internal const bool ENABLE_WORKER_THREAD_INJECTION = false;

        public NodeConfiguration()
        {
            creationTimestamp = DateTime.UtcNow;

            SiloName = "";
            HostNameOrIPAddress = "";
            DNSHostName = Dns.GetHostName();
            Port = 0;
            Generation = 0;
            AddressType = AddressFamily.InterNetwork;
            ProxyGatewayEndpoint = null;

            MaxActiveThreads = DEFAULT_MAX_ACTIVE_THREADS;
            DelayWarningThreshold = TimeSpan.FromMilliseconds(10000); // 10,000 milliseconds
            ActivationSchedulingQuantum = DEFAULT_ACTIVATION_SCHEDULING_QUANTUM;
            TurnWarningLengthThreshold = TimeSpan.FromMilliseconds(200);
            EnableWorkerThreadInjection = ENABLE_WORKER_THREAD_INJECTION;

            LoadSheddingEnabled = false;
            LoadSheddingLimit = 95;

            DefaultTraceLevel = Severity.Info;
            TraceLevelOverrides = new List<Tuple<string, Severity>>();
            TraceToConsole = ConsoleText.IsConsoleAvailable;
            TraceFilePattern = "{0}-{1}.log";
            LargeMessageWarningThreshold = Constants.LARGE_OBJECT_HEAP_THRESHOLD;
            PropagateActivityId = Constants.DEFAULT_PROPAGATE_E2E_ACTIVITY_ID;
            BulkMessageLimit = Constants.DEFAULT_LOGGER_BULK_MESSAGE_LIMIT;

            StatisticsMetricsTableWriteInterval = DEFAULT_STATS_METRICS_TABLE_WRITE_PERIOD;
            StatisticsPerfCountersWriteInterval = DEFAULT_STATS_PERF_COUNTERS_WRITE_PERIOD;
            StatisticsLogWriteInterval = DEFAULT_STATS_LOG_WRITE_PERIOD;
            StatisticsWriteLogStatisticsToTable = true;
            StatisticsCollectionLevel = DEFAULT_STATS_COLLECTION_LEVEL;

            LimitManager = new LimitManager();

            MinDotNetThreadPoolSize = DEFAULT_MIN_DOT_NET_THREAD_POOL_SIZE;

            // .NET ServicePointManager settings / optimizations
            Expect100Continue = false;
            DefaultConnectionLimit = DEFAULT_MIN_DOT_NET_CONNECTION_LIMIT;
            UseNagleAlgorithm = false;

            AdditionalAssemblyDirectories = new Dictionary<string, SearchOption>();
            ExcludedGrainTypes = new List<string>();
        }

        public NodeConfiguration(NodeConfiguration other)
        {
            creationTimestamp = other.creationTimestamp;

            SiloName = other.SiloName;
            HostNameOrIPAddress = other.HostNameOrIPAddress;
            DNSHostName = other.DNSHostName;
            Port = other.Port;
            Generation = other.Generation;
            AddressType = other.AddressType;
            ProxyGatewayEndpoint = other.ProxyGatewayEndpoint;

            MaxActiveThreads = other.MaxActiveThreads;
            DelayWarningThreshold = other.DelayWarningThreshold;
            ActivationSchedulingQuantum = other.ActivationSchedulingQuantum;
            TurnWarningLengthThreshold = other.TurnWarningLengthThreshold;
            EnableWorkerThreadInjection = other.EnableWorkerThreadInjection;

            LoadSheddingEnabled = other.LoadSheddingEnabled;
            LoadSheddingLimit = other.LoadSheddingLimit;

            DefaultTraceLevel = other.DefaultTraceLevel;
            TraceLevelOverrides = new List<Tuple<string, Severity>>(other.TraceLevelOverrides);
            TraceToConsole = other.TraceToConsole;
            TraceFilePattern = other.TraceFilePattern;
            TraceFileName = other.TraceFileName;
            LargeMessageWarningThreshold = other.LargeMessageWarningThreshold;
            PropagateActivityId = other.PropagateActivityId;
            BulkMessageLimit = other.BulkMessageLimit;

            StatisticsProviderName = other.StatisticsProviderName;
            StatisticsMetricsTableWriteInterval = other.StatisticsMetricsTableWriteInterval;
            StatisticsPerfCountersWriteInterval = other.StatisticsPerfCountersWriteInterval;
            StatisticsLogWriteInterval = other.StatisticsLogWriteInterval;
            StatisticsWriteLogStatisticsToTable = other.StatisticsWriteLogStatisticsToTable;
            StatisticsCollectionLevel = other.StatisticsCollectionLevel;

            LimitManager = new LimitManager(other.LimitManager); // Shallow copy

            Subnet = other.Subnet;

            MinDotNetThreadPoolSize = other.MinDotNetThreadPoolSize;
            Expect100Continue = other.Expect100Continue;
            DefaultConnectionLimit = other.DefaultConnectionLimit;
            UseNagleAlgorithm = other.UseNagleAlgorithm;

            StartupTypeName = other.StartupTypeName;
            AdditionalAssemblyDirectories = new Dictionary<string, SearchOption>(other.AdditionalAssemblyDirectories);
            ExcludedGrainTypes = other.ExcludedGrainTypes.ToList();
        }

        public override string ToString()
        {
            var sb = new StringBuilder();

            sb.Append("   Silo Name: ").AppendLine(SiloName);
            sb.Append("   Generation: ").Append(Generation).AppendLine();
            sb.Append("   Host Name or IP Address: ").AppendLine(HostNameOrIPAddress);
            sb.Append("   DNS Host Name: ").AppendLine(DNSHostName);
            sb.Append("   Port: ").Append(Port).AppendLine();
            sb.Append("   Subnet: ").Append(Subnet == null ? "" : Subnet.ToStrings(x => x.ToString(), ".")).AppendLine();
            sb.Append("   Preferred Address Family: ").Append(AddressType).AppendLine();
            if (IsGatewayNode)
            {
                sb.Append("   Proxy Gateway: ").Append(ProxyGatewayEndpoint.ToString()).AppendLine();
            }
            else
            {
                sb.Append("   IsGatewayNode: ").Append(IsGatewayNode).AppendLine();
            }
            sb.Append("   IsPrimaryNode: ").Append(IsPrimaryNode).AppendLine();
            sb.Append("   Scheduler: ").AppendLine();
            sb.Append("      ").Append("   Max Active Threads: ").Append(MaxActiveThreads).AppendLine();
            sb.Append("      ").Append("   Processor Count: ").Append(System.Environment.ProcessorCount).AppendLine();
            sb.Append("      ").Append("   Delay Warning Threshold: ").Append(DelayWarningThreshold).AppendLine();
            sb.Append("      ").Append("   Activation Scheduling Quantum: ").Append(ActivationSchedulingQuantum).AppendLine();
            sb.Append("      ").Append("   Turn Warning Length Threshold: ").Append(TurnWarningLengthThreshold).AppendLine();
            sb.Append("      ").Append("   Inject More Worker Threads: ").Append(EnableWorkerThreadInjection).AppendLine();
            sb.Append("      ").Append("   MinDotNetThreadPoolSize: ").Append(MinDotNetThreadPoolSize).AppendLine();
#if !NETSTANDARD_TODO
            int workerThreads;
            int completionPortThreads;
            ThreadPool.GetMinThreads(out workerThreads, out completionPortThreads);
            sb.Append("      ").AppendFormat("   .NET thread pool sizes - Min: Worker Threads={0} Completion Port Threads={1}", workerThreads, completionPortThreads).AppendLine();
            ThreadPool.GetMaxThreads(out workerThreads, out completionPortThreads);
            sb.Append("      ").AppendFormat("   .NET thread pool sizes - Max: Worker Threads={0} Completion Port Threads={1}", workerThreads, completionPortThreads).AppendLine();
#endif
            sb.Append("      ").AppendFormat("   .NET ServicePointManager - DefaultConnectionLimit={0} Expect100Continue={1} UseNagleAlgorithm={2}", DefaultConnectionLimit, Expect100Continue, UseNagleAlgorithm).AppendLine();
            sb.Append("   Load Shedding Enabled: ").Append(LoadSheddingEnabled).AppendLine();
            sb.Append("   Load Shedding Limit: ").Append(LoadSheddingLimit).AppendLine();
            sb.Append("   SiloShutdownEventName: ").Append(SiloShutdownEventName).AppendLine();
            sb.Append("   Debug: ").AppendLine();
            sb.Append(ConfigUtilities.TraceConfigurationToString(this));
            sb.Append(ConfigUtilities.IStatisticsConfigurationToString(this));
            sb.Append(LimitManager);
            return sb.ToString();
        }

        internal void Load(XmlElement root)
        {
            SiloName = root.LocalName.Equals("Override") ? root.GetAttribute("Node") : DEFAULT_NODE_NAME;

            foreach (XmlNode c in root.ChildNodes)
            {
                var child = c as XmlElement;

                if (child == null) continue; // Skip comment lines

                switch (child.LocalName)
                {
                    case "Networking":
                        if (child.HasAttribute("Address"))
                        {
                            HostNameOrIPAddress = child.GetAttribute("Address");
                        }
                        if (child.HasAttribute("Port"))
                        {
                            Port = ConfigUtilities.ParseInt(child.GetAttribute("Port"),
                                "Non-numeric Port attribute value on Networking element for " + SiloName);
                        }
                        if (child.HasAttribute("PreferredFamily"))
                        {
                            AddressType = ConfigUtilities.ParseEnum<AddressFamily>(child.GetAttribute("PreferredFamily"),
                                "Invalid preferred address family on Networking node. Valid choices are 'InterNetwork' and 'InterNetworkV6'");
                        }
                        break;
                    case "ProxyingGateway":
                        ProxyGatewayEndpoint = ConfigUtilities.ParseIPEndPoint(child, Subnet).GetResult();
                        break;
                    case "Scheduler":
                        if (child.HasAttribute("MaxActiveThreads"))
                        {
                            MaxActiveThreads = ConfigUtilities.ParseInt(child.GetAttribute("MaxActiveThreads"),
                                "Non-numeric MaxActiveThreads attribute value on Scheduler element for " + SiloName);
                            if (MaxActiveThreads < 1)
                            {
                                MaxActiveThreads = DEFAULT_MAX_ACTIVE_THREADS;
                            }
                        }
                        if (child.HasAttribute("DelayWarningThreshold"))
                        {
                            DelayWarningThreshold = ConfigUtilities.ParseTimeSpan(child.GetAttribute("DelayWarningThreshold"),
                                "Non-numeric DelayWarningThreshold attribute value on Scheduler element for " + SiloName);
                        }
                        if (child.HasAttribute("ActivationSchedulingQuantum"))
                        {
                            ActivationSchedulingQuantum = ConfigUtilities.ParseTimeSpan(child.GetAttribute("ActivationSchedulingQuantum"),
                                "Non-numeric ActivationSchedulingQuantum attribute value on Scheduler element for " + SiloName);
                        }
                        if (child.HasAttribute("TurnWarningLengthThreshold"))
                        {
                            TurnWarningLengthThreshold = ConfigUtilities.ParseTimeSpan(child.GetAttribute("TurnWarningLengthThreshold"),
                                "Non-numeric TurnWarningLengthThreshold attribute value on Scheduler element for " + SiloName);
                        }
                        if (child.HasAttribute("MinDotNetThreadPoolSize"))
                        {
                            MinDotNetThreadPoolSize = ConfigUtilities.ParseInt(child.GetAttribute("MinDotNetThreadPoolSize"),
                                "Invalid ParseInt MinDotNetThreadPoolSize value on Scheduler element for " + SiloName);
                        }
                        if (child.HasAttribute("Expect100Continue"))
                        {
                            Expect100Continue = ConfigUtilities.ParseBool(child.GetAttribute("Expect100Continue"),
                                "Invalid ParseBool Expect100Continue value on Scheduler element for " + SiloName);
                        }
                        if (child.HasAttribute("DefaultConnectionLimit"))
                        {
                            DefaultConnectionLimit = ConfigUtilities.ParseInt(child.GetAttribute("DefaultConnectionLimit"),
                                "Invalid ParseInt DefaultConnectionLimit value on Scheduler element for " + SiloName);
                        }
                        if (child.HasAttribute("UseNagleAlgorithm "))
                        {
                            UseNagleAlgorithm = ConfigUtilities.ParseBool(child.GetAttribute("UseNagleAlgorithm "),
                                "Invalid ParseBool UseNagleAlgorithm value on Scheduler element for " + SiloName);
                        }
                        break;
                    case "LoadShedding":
                        if (child.HasAttribute("Enabled"))
                        {
                            LoadSheddingEnabled = ConfigUtilities.ParseBool(child.GetAttribute("Enabled"),
                                "Invalid boolean value for Enabled attribute on LoadShedding attribute for " + SiloName);
                        }
                        if (child.HasAttribute("LoadLimit"))
                        {
                            LoadSheddingLimit = ConfigUtilities.ParseInt(child.GetAttribute("LoadLimit"),
                                "Invalid integer value for LoadLimit attribute on LoadShedding attribute for " + SiloName);
                            if (LoadSheddingLimit < 0)
                            {
                                LoadSheddingLimit = 0;
                            }
                            if (LoadSheddingLimit > 100)
                            {
                                LoadSheddingLimit = 100;
                            }
                        }
                        break;
                    case "Tracing":
                        ConfigUtilities.ParseTracing(this, child, SiloName);
                        break;
                    case "Statistics":
                        ConfigUtilities.ParseStatistics(this, child, SiloName);
                        break;
                    case "Limits":
                        ConfigUtilities.ParseLimitValues(LimitManager, child, SiloName);
                        break;
                    case "Startup":
                        if (child.HasAttribute("Type"))
                        {
                            StartupTypeName = child.GetAttribute("Type");
                        }
                        break;
                    case "Telemetry":
                        ConfigUtilities.ParseTelemetry(child);
                        break;
                    case "AdditionalAssemblyDirectories":
                        ConfigUtilities.ParseAdditionalAssemblyDirectories(AdditionalAssemblyDirectories, child);
                        break;
                }
            }
        }
    }
}
