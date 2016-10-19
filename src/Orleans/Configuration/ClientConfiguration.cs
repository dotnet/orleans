using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Xml;
using Orleans.Providers;

namespace Orleans.Runtime.Configuration
{
    /// <summary>
    /// Orleans client configuration parameters.
    /// </summary>
    public class ClientConfiguration : MessagingConfiguration, ITraceConfiguration, IStatisticsConfiguration
    {
        /// <summary>
        /// Specifies the type of the gateway provider.
        /// </summary>
        public enum GatewayProviderType
        {
            /// <summary>No provider specified</summary>
            None,

            /// <summary>use Azure, requires SystemStore element</summary>
            AzureTable,

            /// <summary>use SQL, requires SystemStore element</summary>
            SqlServer,

            /// <summary>use ZooKeeper, requires SystemStore element</summary>
            ZooKeeper,

            /// <summary>use Config based static list, requires Config element(s)</summary>
            Config,

            /// <summary>use provider from third-party assembly</summary>
            Custom
        }

        /// <summary>
        /// The name of this client.
        /// </summary>
        public static string ClientName = "Client";

        private string traceFilePattern;
        private readonly DateTime creationTimestamp;

        /// <summary>Gets the configuration source file path</summary>
        public string SourceFile { get; private set; }

        /// <summary>
        /// The list fo the gateways to use.
        /// Each GatewayNode element specifies an outside grain client gateway node.
        /// If outside (non-Orleans) clients are to connect to the Orleans system, then at least one gateway node must be specified.
        /// Additional gateway nodes may be specified if desired, and will add some failure resilience and scalability.
        /// If multiple gateways are specified, then each client will select one from the list at random.
        /// </summary>
        public IList<IPEndPoint> Gateways { get; set; }
        /// <summary>
        /// </summary>
        public int PreferedGatewayIndex { get; set; }
        /// <summary>
        /// </summary>
        public GatewayProviderType GatewayProvider { get; set; }

        /// <summary>
        /// Specifies a unique identifier of this deployment.
        /// If the silos are deployed on Azure (run as workers roles), deployment id is set automatically by Azure runtime, 
        /// accessible to the role via RoleEnvironment.DeploymentId static variable and is passed to the silo automatically by the role via config. 
        /// So if the silos are run as Azure roles this variable should not be specified in the OrleansConfiguration.xml (it will be overwritten if specified).
        /// If the silos are deployed on the cluster and not as Azure roles, this variable should be set by a deployment script in the OrleansConfiguration.xml file.
        /// </summary>
        public string DeploymentId { get; set; }
        /// <summary>
        /// Specifies the connection string for the gateway provider.
        /// If the silos are deployed on Azure (run as workers roles), DataConnectionString may be specified via RoleEnvironment.GetConfigurationSettingValue("DataConnectionString");
        /// In such a case it is taken from there and passed to the silo automatically by the role via config.
        /// So if the silos are run as Azure roles and this config is specified via RoleEnvironment, 
        /// this variable should not be specified in the OrleansConfiguration.xml (it will be overwritten if specified).
        /// If the silos are deployed on the cluster and not as Azure roles,  this variable should be set in the OrleansConfiguration.xml file.
        /// If not set at all, DevelopmentStorageAccount will be used.
        /// </summary>
        public string DataConnectionString { get; set; }

        /// <summary>
        /// When using ADO, identifies the underlying data provider for the gateway provider. This three-part naming syntax is also used when creating a new factory 
        /// and for identifying the provider in an application configuration file so that the provider name, along with its associated 
        /// connection string, can be retrieved at run time. https://msdn.microsoft.com/en-us/library/dd0w4a2z%28v=vs.110%29.aspx
        /// </summary>
        public string AdoInvariant { get; set; }

        public string CustomGatewayProviderAssemblyName { get; set; }

        /// <inheritdoc />
        public Severity DefaultTraceLevel { get; set; }

        /// <inheritdoc />
        public IList<Tuple<string, Severity>> TraceLevelOverrides { get; private set; }

        /// <inheritdoc />
        public bool TraceToConsole { get; set; }

        /// <inheritdoc />
        public int LargeMessageWarningThreshold { get; set; }

        /// <inheritdoc />
        public bool PropagateActivityId { get; set; }

        /// <inheritdoc />
        public int BulkMessageLimit { get; set; }

        /// <summary>
        /// </summary>
        public AddressFamily PreferredFamily { get; set; }
        /// <summary>
        /// The Interface attribute specifies the name of the network interface to use to work out an IP address for this machine.
        /// </summary>
        public string NetInterface { get; private set; }
        /// <summary>
        /// The Port attribute specifies the specific listen port for this client machine.
        /// If value is zero, then a random machine-assigned port number will be used.
        /// </summary>
        public int Port { get; private set; }
        /// <summary>Gets the true host name, no IP address. It equals Dns.GetHostName()</summary>
        public string DNSHostName { get; private set; }
        /// <summary>
        /// </summary>
        public TimeSpan GatewayListRefreshPeriod { get; set; }

        public string StatisticsProviderName { get; set; }
        public TimeSpan StatisticsMetricsTableWriteInterval { get; set; }
        public TimeSpan StatisticsPerfCountersWriteInterval { get; set; }
        public TimeSpan StatisticsLogWriteInterval { get; set; }
        public bool StatisticsWriteLogStatisticsToTable { get; set; }
        public StatisticsLevel StatisticsCollectionLevel { get; set; }

        public LimitManager LimitManager { get; private set; }

        private static readonly TimeSpan DEFAULT_GATEWAY_LIST_REFRESH_PERIOD = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan DEFAULT_STATS_METRICS_TABLE_WRITE_PERIOD = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan DEFAULT_STATS_PERF_COUNTERS_WRITE_PERIOD = Constants.INFINITE_TIMESPAN;
        private static readonly TimeSpan DEFAULT_STATS_LOG_WRITE_PERIOD = TimeSpan.FromMinutes(5);

        /// <summary>
        /// </summary>
        public bool UseAzureSystemStore 
        { 
            get { 
                return GatewayProvider == GatewayProviderType.AzureTable 
                       && !String.IsNullOrWhiteSpace(DeploymentId) 
                       && !String.IsNullOrWhiteSpace(DataConnectionString); 
            } 
        }

        /// <summary>
        /// </summary>
        public bool UseSqlSystemStore
        {
            get
            {
                return GatewayProvider == GatewayProviderType.SqlServer
                && !String.IsNullOrWhiteSpace(DeploymentId)
                && !String.IsNullOrWhiteSpace(DataConnectionString);
            }
        }

        private bool HasStaticGateways { get { return Gateways != null && Gateways.Count > 0; } }
        /// <summary>
        /// </summary>
        public IDictionary<string, ProviderCategoryConfiguration> ProviderConfigurations { get; set; }

        /// <inheritdoc />
        public string TraceFilePattern
        {
            get { return traceFilePattern; }
            set
            {
                traceFilePattern = value;
                ConfigUtilities.SetTraceFileName(this, ClientName, this.creationTimestamp);
            }
        }

        /// <inheritdoc />
        public string TraceFileName { get; set; }

        /// <summary>Initializes a new instance of <see cref="ClientConfiguration"/>.</summary>
        public ClientConfiguration()
            : base(false)
        {
            creationTimestamp = DateTime.UtcNow;
            SourceFile = null;
            PreferedGatewayIndex = -1;
            Gateways = new List<IPEndPoint>();
            GatewayProvider = GatewayProviderType.None;
            PreferredFamily = AddressFamily.InterNetwork;
            NetInterface = null;
            Port = 0;
            DNSHostName = Dns.GetHostName();
            DeploymentId = "";
            DataConnectionString = "";
            // Assume the ado invariant is for sql server storage if not explicitly specified
            AdoInvariant = Constants.INVARIANT_NAME_SQL_SERVER;

            DefaultTraceLevel = Severity.Info;
            TraceLevelOverrides = new List<Tuple<string, Severity>>();
            TraceToConsole = true;
            TraceFilePattern = "{0}-{1}.log";
            LargeMessageWarningThreshold = Constants.LARGE_OBJECT_HEAP_THRESHOLD;
            PropagateActivityId = Constants.DEFAULT_PROPAGATE_E2E_ACTIVITY_ID;
            BulkMessageLimit = Constants.DEFAULT_LOGGER_BULK_MESSAGE_LIMIT;

            GatewayListRefreshPeriod = DEFAULT_GATEWAY_LIST_REFRESH_PERIOD;
            StatisticsProviderName = null;
            StatisticsMetricsTableWriteInterval = DEFAULT_STATS_METRICS_TABLE_WRITE_PERIOD;
            StatisticsPerfCountersWriteInterval = DEFAULT_STATS_PERF_COUNTERS_WRITE_PERIOD;
            StatisticsLogWriteInterval = DEFAULT_STATS_LOG_WRITE_PERIOD;
            StatisticsWriteLogStatisticsToTable = true;
            StatisticsCollectionLevel = NodeConfiguration.DEFAULT_STATS_COLLECTION_LEVEL;
            LimitManager = new LimitManager();
            ProviderConfigurations = new Dictionary<string, ProviderCategoryConfiguration>();
        }

        public void Load(TextReader input)
        {
            var xml = new XmlDocument();
            var xmlReader = XmlReader.Create(input);
            xml.Load(xmlReader);
            XmlElement root = xml.DocumentElement;

            LoadFromXml(root);
        }

        internal void LoadFromXml(XmlElement root)
        {
            foreach (XmlNode node in root.ChildNodes)
            {
                var child = node as XmlElement;
                if (child != null)
                {
                    switch (child.LocalName)
                    {
                        case "Gateway":
                            Gateways.Add(ConfigUtilities.ParseIPEndPoint(child).GetResult());
                            if (GatewayProvider == GatewayProviderType.None)
                            {
                                GatewayProvider = GatewayProviderType.Config;
                            }
                            break;
                        case "Azure":
                            // Throw exception with explicit deprecation error message
                            throw new OrleansException(
                                "The Azure element has been deprecated -- use SystemStore element instead.");
                        case "SystemStore":
                            if (child.HasAttribute("SystemStoreType"))
                            {
                                var sst = child.GetAttribute("SystemStoreType");
                                GatewayProvider = (GatewayProviderType)Enum.Parse(typeof(GatewayProviderType), sst);
                            }
                            if (child.HasAttribute("CustomGatewayProviderAssemblyName"))
                            {
                                CustomGatewayProviderAssemblyName = child.GetAttribute("CustomGatewayProviderAssemblyName");
                                if (CustomGatewayProviderAssemblyName.EndsWith(".dll"))
                                    throw new FormatException("Use fully qualified assembly name for \"CustomGatewayProviderAssemblyName\"");
                                if (GatewayProvider != GatewayProviderType.Custom)
                                    throw new FormatException("SystemStoreType should be \"Custom\" when CustomGatewayProviderAssemblyName is specified");
                            }
                            if (child.HasAttribute("DeploymentId"))
                            {
                                DeploymentId = child.GetAttribute("DeploymentId");
                            }
                            if (child.HasAttribute(Constants.DATA_CONNECTION_STRING_NAME))
                            {
                                DataConnectionString = child.GetAttribute(Constants.DATA_CONNECTION_STRING_NAME);
                                if (String.IsNullOrWhiteSpace(DataConnectionString))
                                {
                                    throw new FormatException("SystemStore.DataConnectionString cannot be blank");
                                }
                                if (GatewayProvider == GatewayProviderType.None)
                                {
                                    // Assume the connection string is for Azure storage if not explicitly specified
                                    GatewayProvider = GatewayProviderType.AzureTable;
                                }
                            }
                            if (child.HasAttribute(Constants.ADO_INVARIANT_NAME))
                            {
                                AdoInvariant = child.GetAttribute(Constants.ADO_INVARIANT_NAME);
                                if (String.IsNullOrWhiteSpace(AdoInvariant))
                                {
                                    throw new FormatException("SystemStore.AdoInvariant cannot be blank");
                                }
                            }
                            break;

                        case "Tracing":
                            ConfigUtilities.ParseTracing(this, child, ClientName);
                            break;
                        case "Statistics":
                            ConfigUtilities.ParseStatistics(this, child, ClientName);
                            break;
                        case "Limits":
                            ConfigUtilities.ParseLimitValues(LimitManager, child, ClientName);
                            break;
                        case "Debug":
                            break;
                        case "Messaging":
                            base.Load(child);
                            break;
                        case "LocalAddress":
                            if (child.HasAttribute("PreferredFamily"))
                            {
                                PreferredFamily = ConfigUtilities.ParseEnum<AddressFamily>(child.GetAttribute("PreferredFamily"),
                                    "Invalid address family for the PreferredFamily attribute on the LocalAddress element");
                            }
                            else
                            {
                                throw new FormatException("Missing PreferredFamily attribute on the LocalAddress element");
                            }
                            if (child.HasAttribute("Interface"))
                            {
                                NetInterface = child.GetAttribute("Interface");
                            }
                            if (child.HasAttribute("Port"))
                            {
                                Port = ConfigUtilities.ParseInt(child.GetAttribute("Port"),
                                    "Invalid integer value for the Port attribute on the LocalAddress element");
                            }
                            break;
                        case "Telemetry":
                            ConfigUtilities.ParseTelemetry(child);
                            break;
                        default:
                            if (child.LocalName.EndsWith("Providers", StringComparison.Ordinal))
                            {
                                var providerCategory = ProviderCategoryConfiguration.Load(child);

                                if (ProviderConfigurations.ContainsKey(providerCategory.Name))
                                {
                                    var existingCategory = ProviderConfigurations[providerCategory.Name];
                                    existingCategory.Merge(providerCategory);
                                }
                                else
                                {
                                    ProviderConfigurations.Add(providerCategory.Name, providerCategory);
                                }
                            }
                            break;
                    }
                }
            }
        }

        /// <summary>
        /// </summary>
        public static ClientConfiguration LoadFromFile(string fileName)
        {
            if (fileName == null)
            { return null; }

            using (TextReader input = File.OpenText(fileName))
            {
                var config = new ClientConfiguration();
                config.Load(input);
                config.SourceFile = fileName;
                return config;
            }
            
        }

        /// <summary>
        /// Registers a given type of <typeparamref name="T"/> where <typeparamref name="T"/> is stream provider
        /// </summary>
        /// <typeparam name="T">Non-abstract type which implements <see cref="Orleans.Streams.IStreamProvider"/> stream</typeparam>
        /// <param name="providerName">Name of the stream provider</param>
        /// <param name="properties">Properties that will be passed to stream provider upon initialization</param>
        public void RegisterStreamProvider<T>(string providerName, IDictionary<string, string> properties = null) where T : Orleans.Streams.IStreamProvider
        {
            TypeInfo providerTypeInfo = typeof(T).GetTypeInfo();
            if (providerTypeInfo.IsAbstract ||
                providerTypeInfo.IsGenericType ||
                !typeof(Orleans.Streams.IStreamProvider).IsAssignableFrom(typeof(T)))
                throw new ArgumentException("Expected non-generic, non-abstract type which implements IStreamProvider interface", "typeof(T)");

            ProviderConfigurationUtility.RegisterProvider(ProviderConfigurations, ProviderCategoryConfiguration.STREAM_PROVIDER_CATEGORY_NAME, providerTypeInfo.FullName, providerName, properties);
        }

        /// <summary>
        /// Registers a given stream provider.
        /// </summary>
        /// <param name="providerTypeFullName">Full name of the stream provider type</param>
        /// <param name="providerName">Name of the stream provider</param>
        /// <param name="properties">Properties that will be passed to the stream provider upon initialization </param>
        public void RegisterStreamProvider(string providerTypeFullName, string providerName, IDictionary<string, string> properties = null)
        {
            ProviderConfigurationUtility.RegisterProvider(ProviderConfigurations, ProviderCategoryConfiguration.STREAM_PROVIDER_CATEGORY_NAME, providerTypeFullName, providerName, properties);
        }

        /// <summary>
        /// Retrieves an existing provider configuration
        /// </summary>
        /// <param name="providerTypeFullName">Full name of the stream provider type</param>
        /// <param name="providerName">Name of the stream provider</param>
        /// <param name="config">The provider configuration, if exists</param>
        /// <returns>True if a configuration for this provider already exists, false otherwise.</returns>
        public bool TryGetProviderConfiguration(string providerTypeFullName, string providerName, out IProviderConfiguration config)
        {
            return ProviderConfigurationUtility.TryGetProviderConfiguration(ProviderConfigurations, providerTypeFullName, providerName, out config);
        }

        /// <summary>
        /// Retrieves an enumeration of all currently configured provider configurations.
        /// </summary>
        /// <returns>An enumeration of all currently configured provider configurations.</returns>
        public IEnumerable<IProviderConfiguration> GetAllProviderConfigurations()
        {
            return ProviderConfigurationUtility.GetAllProviderConfigurations(ProviderConfigurations);
        }

        /// <summary>
        /// Loads the configuration from the standard paths, looking up the directory hierarchy
        /// </summary>
        /// <returns>Client configuration data if a configuration file was found.</returns>
        /// <exception cref="FileNotFoundException">Thrown if no configuration file could be found in any of the standard locations</exception>
        public static ClientConfiguration StandardLoad()
        {
            var fileName = ConfigUtilities.FindConfigFile(false); // Throws FileNotFoundException
            return LoadFromFile(fileName);
        }

        /// <summary>Returns a detailed human readable string that represents the current configuration. It does not contain every single configuration knob.</summary>
        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Platform version info:").Append(ConfigUtilities.RuntimeVersionInfo());
            sb.Append("   Host: ").AppendLine(Dns.GetHostName());
            sb.Append("   Processor Count: ").Append(System.Environment.ProcessorCount).AppendLine();

            sb.AppendLine("Client Configuration:");
            sb.Append("   Config File Name: ").AppendLine(string.IsNullOrEmpty(SourceFile) ? "" : Path.GetFullPath(SourceFile));
            sb.Append("   Start time: ").AppendLine(LogFormatter.PrintDate(DateTime.UtcNow));
            sb.Append("   Gateway Provider: ").Append(GatewayProvider);
            if (GatewayProvider == GatewayProviderType.None)
            {
                sb.Append(".   Gateway Provider that will be used instead: ").Append(GatewayProviderToUse);
            }
            sb.AppendLine();
            if (Gateways != null && Gateways.Count > 0 )
            {
                sb.AppendFormat("   Gateways[{0}]:", Gateways.Count).AppendLine();
                foreach (var endpoint in Gateways)
                {
                    sb.Append("      ").AppendLine(endpoint.ToString());
                }
            }
            else
            {
                sb.Append("   Gateways: ").AppendLine("Unspecified");
            }
            sb.Append("   Preferred Gateway Index: ").AppendLine(PreferedGatewayIndex.ToString());
            if (Gateways != null && PreferedGatewayIndex >= 0 && PreferedGatewayIndex < Gateways.Count)
            {
                sb.Append("   Preferred Gateway Address: ").AppendLine(Gateways[PreferedGatewayIndex].ToString());
            }
            sb.Append("   GatewayListRefreshPeriod: ").Append(GatewayListRefreshPeriod).AppendLine();
            if (!String.IsNullOrEmpty(DeploymentId) || !String.IsNullOrEmpty(DataConnectionString))
            {
                sb.Append("   Azure:").AppendLine();
                sb.Append("      DeploymentId: ").Append(DeploymentId).AppendLine();
                string dataConnectionInfo = ConfigUtilities.RedactConnectionStringInfo(DataConnectionString); // Don't print Azure account keys in log files
                sb.Append("      DataConnectionString: ").Append(dataConnectionInfo).AppendLine();
            }
            if (!string.IsNullOrWhiteSpace(NetInterface))
            {
                sb.Append("   Network Interface: ").AppendLine(NetInterface);
            }
            if (Port != 0)
            {
                sb.Append("   Network Port: ").Append(Port).AppendLine();
            }
            sb.Append("   Preferred Address Family: ").AppendLine(PreferredFamily.ToString());
            sb.Append("   DNS Host Name: ").AppendLine(DNSHostName);
            sb.Append("   Client Name: ").AppendLine(ClientName);
            sb.Append(ConfigUtilities.TraceConfigurationToString(this));
            sb.Append(ConfigUtilities.IStatisticsConfigurationToString(this));
            sb.Append(LimitManager);
            sb.AppendFormat(base.ToString());
#if !NETSTANDARD
            sb.Append("   .NET: ").AppendLine();
            int workerThreads;
            int completionPortThreads;
            ThreadPool.GetMinThreads(out workerThreads, out completionPortThreads);
            sb.AppendFormat("       .NET thread pool sizes - Min: Worker Threads={0} Completion Port Threads={1}", workerThreads, completionPortThreads).AppendLine();
            ThreadPool.GetMaxThreads(out workerThreads, out completionPortThreads);
            sb.AppendFormat("       .NET thread pool sizes - Max: Worker Threads={0} Completion Port Threads={1}", workerThreads, completionPortThreads).AppendLine();
#endif
            sb.AppendFormat("   Providers:").AppendLine();
            sb.Append(ProviderConfigurationUtility.PrintProviderConfigurations(ProviderConfigurations));
            return sb.ToString();
        }

        internal GatewayProviderType GatewayProviderToUse
        {
            get
            {
                // order is important here for establishing defaults.
                if (GatewayProvider != GatewayProviderType.None) return GatewayProvider;
                if (UseAzureSystemStore) return GatewayProviderType.AzureTable;
                return HasStaticGateways ? GatewayProviderType.Config : GatewayProviderType.None;
            }
        }

        internal void CheckGatewayProviderSettings()
        {
            switch (GatewayProvider)
            {
                case GatewayProviderType.AzureTable:
                    if (!UseAzureSystemStore)
                        throw new ArgumentException("Config specifies Azure based GatewayProviderType, but Azure element is not specified or not complete.", "GatewayProvider");
                    break;
                case GatewayProviderType.Config:
                    if (!HasStaticGateways)
                        throw new ArgumentException("Config specifies Config based GatewayProviderType, but Gateway element(s) is/are not specified.", "GatewayProvider");
                    break;
                case GatewayProviderType.Custom:
                    if (String.IsNullOrEmpty(CustomGatewayProviderAssemblyName))
                        throw new ArgumentException("Config specifies Custom GatewayProviderType, but CustomGatewayProviderAssemblyName attribute is not specified", "GatewayProvider");
                    break;
                case GatewayProviderType.None:
                    if (!UseAzureSystemStore && !HasStaticGateways)
                        throw new ArgumentException("Config does not specify GatewayProviderType, and also does not have the adequate defaults: no Azure and or Gateway element(s) are specified.","GatewayProvider");
                    break;
                case GatewayProviderType.SqlServer:
                    if (!UseSqlSystemStore)
                        throw new ArgumentException("Config specifies SqlServer based GatewayProviderType, but DeploymentId or DataConnectionString are not specified or not complete.", "GatewayProvider");
                    break;
                case GatewayProviderType.ZooKeeper:
                    break;
            }
        }

        /// <summary>
        /// Retuurns a ClientConfiguration object for connecting to a local silo (for testing).
        /// </summary>
        /// <param name="gatewayPort">Client gateway TCP port</param>
        /// <returns>ClientConfiguration object that can be passed to GrainClient class for initialization</returns>
        public static ClientConfiguration LocalhostSilo(int gatewayPort = 40000)
        {
            var config = new ClientConfiguration {GatewayProvider = GatewayProviderType.Config};
            config.Gateways.Add(new IPEndPoint(IPAddress.Loopback, gatewayPort));

            return config;
        }
    }
}
