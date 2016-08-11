using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using Orleans.Providers;
using System.Reflection;
using System.Threading;
using Orleans.Configuration.New;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime.Configuration.New
{
    /// <summary>
    /// Orleans client configuration parameters.
    /// </summary>
    public class ClientConfigurator
    {
        /// <summary>
        /// The name of this client.
        /// </summary>
        public static string ClientName = "Client";
        
        private readonly DateTime creationTimestamp;

        public string SourceFile { get; private set; }

        /// <summary>
        /// The list fo the gateways to use.
        /// Each GatewayNode element specifies an outside grain client gateway node.
        /// If outside (non-Orleans) clients are to connect to the Orleans system, then at least one gateway node must be specified.
        /// Additional gateway nodes may be specified if desired, and will add some failure resilience and scalability.
        /// If multiple gateways are specified, then each client will select one from the list at random.
        /// </summary>
        public List<IPEndPoint> Gateways { get; } = new List<IPEndPoint>();
        /// <summary>
        /// </summary>
        public int PreferedGatewayIndex { get; set; }
        
        /// <summary>
        /// </summary>
        public string DNSHostName { get; private set; } // This is a true host name, no IP address. It is NOT settable, equals Dns.GetHostName().
        /// <summary>
        /// </summary>
        public TimeSpan GatewayListRefreshPeriod { get; set; }
        
        public LimitManager LimitManager { get; private set; }
        /// <summary>
        /// </summary>
        public ClientConfiguration.GatewayProviderType GatewayProvider { get; set; }

        private static readonly TimeSpan DEFAULT_GATEWAY_LIST_REFRESH_PERIOD = TimeSpan.FromMinutes(1);

        /// <summary>
        /// </summary>
        public bool UseAzureSystemStore 
        { 
            get { 
                return GatewayProvider == Configuration.ClientConfiguration.GatewayProviderType.AzureTable 
                       && !String.IsNullOrWhiteSpace(Settings.SystemStore.DeploymentId) 
                       && !String.IsNullOrWhiteSpace(Settings.SystemStore.DataConnectionString); 
            } 
        }

        /// <summary>
        /// </summary>
        public bool UseSqlSystemStore
        {
            get
            {
                return GatewayProvider == Configuration.ClientConfiguration.GatewayProviderType.SqlServer
                && !String.IsNullOrWhiteSpace(Settings.SystemStore.DeploymentId)
                && !String.IsNullOrWhiteSpace(Settings.SystemStore.DataConnectionString);
            }
        }

        private bool HasStaticGateways { get { return Gateways != null && Gateways.Count > 0; } }
        /// <summary>
        /// </summary>
        public IDictionary<string, ProviderCategoryConfiguration> ProviderConfigurations { get; set; }
        
        /// <summary>
        /// </summary>
        public ClientConfigurator()
        {
            creationTimestamp = DateTime.UtcNow;
            SourceFile = null;
            PreferedGatewayIndex = -1;
            Gateways = new List<IPEndPoint>();
            DNSHostName = Dns.GetHostName();

            GatewayListRefreshPeriod = DEFAULT_GATEWAY_LIST_REFRESH_PERIOD;
            LimitManager = new LimitManager();
            ProviderConfigurations = new Dictionary<string, ProviderCategoryConfiguration>();
        }
        
        
        public ClientSettings Settings { get; private set; }

        public void Build(ClientSettings client)
        {
            Settings = client;

            foreach(var gateway in Settings.Gateways)
            {
                Gateways.Add(gateway.ParseIPEndPoint().GetResult());
                if (GatewayProvider == ClientConfiguration.GatewayProviderType.None)
                {
                    GatewayProvider = ClientConfiguration.GatewayProviderType.Config;
                }
            }
                        
            if (Settings.SystemStore.SystemStoreType.HasValue)
            {
                GatewayProvider = Settings.SystemStore.SystemStoreType.Value;
            }
            if (!string.IsNullOrEmpty(Settings.SystemStore.CustomGatewayProviderAssemblyName))
            {
                if (Settings.SystemStore.CustomGatewayProviderAssemblyName.EndsWith(".dll"))
                    throw new FormatException("Use fully qualified assembly name for \"CustomGatewayProviderAssemblyName\"");
                if (GatewayProvider != ClientConfiguration.GatewayProviderType.Custom)
                    throw new FormatException("SystemStoreType should be \"Custom\" when CustomGatewayProviderAssemblyName is specified");
            }
            if (!string.IsNullOrEmpty(Settings.SystemStore.DataConnectionString))
            { 
                if (GatewayProvider == ClientConfiguration.GatewayProviderType.None)
                {
                    // Assume the connection string is for Azure storage if not explicitly specified
                    GatewayProvider = ClientConfiguration.GatewayProviderType.AzureTable;
                }
            }
                    
                            //ConfigUtilities.ParseTelemetry(child);
                       
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
            if (GatewayProvider == Configuration.ClientConfiguration.GatewayProviderType.None)
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
            if (!String.IsNullOrEmpty(Settings.SystemStore.DeploymentId) || !String.IsNullOrEmpty(Settings.SystemStore.DataConnectionString))
            {
                sb.Append("   Azure:").AppendLine();
                sb.Append("      DeploymentId: ").Append(Settings.SystemStore.DeploymentId).AppendLine();
                string dataConnectionInfo = ConfigUtilities.RedactConnectionStringInfo(Settings.SystemStore.DataConnectionString); // Don't print Azure account keys in log files
                sb.Append("      DataConnectionString: ").Append(dataConnectionInfo).AppendLine();
            }
            if (!string.IsNullOrWhiteSpace(Settings.LocalAddress.Interface))
            {
                sb.Append("   Network Interface: ").AppendLine(Settings.LocalAddress.Interface);
            }
            if (Settings.LocalAddress.Port != 0)
            {
                sb.Append("   Network Port: ").Append(Settings.LocalAddress.Port).AppendLine();
            }
            sb.Append("   Preferred Address Family: ").AppendLine(Settings.LocalAddress.PreferredFamily.ToString());
            sb.Append("   DNS Host Name: ").AppendLine(DNSHostName);
            sb.Append("   Client Name: ").AppendLine(ClientName);
            sb.Append(Settings.Tracing);
            sb.Append(Settings.Statistics);
            sb.Append(LimitManager);
            sb.AppendFormat(base.ToString());
            sb.Append("   .NET: ").AppendLine();
            int workerThreads;
            int completionPortThreads;
            ThreadPool.GetMinThreads(out workerThreads, out completionPortThreads);
            sb.AppendFormat("       .NET thread pool sizes - Min: Worker Threads={0} Completion Port Threads={1}", workerThreads, completionPortThreads).AppendLine();
            ThreadPool.GetMaxThreads(out workerThreads, out completionPortThreads);
            sb.AppendFormat("       .NET thread pool sizes - Max: Worker Threads={0} Completion Port Threads={1}", workerThreads, completionPortThreads).AppendLine();
            sb.AppendFormat("   Providers:").AppendLine();
            sb.Append(ProviderConfigurationUtility.PrintProviderConfigurations(ProviderConfigurations));
            return sb.ToString();
        }

        internal Configuration.ClientConfiguration.GatewayProviderType GatewayProviderToUse
        {
            get
            {
                // order is important here for establishing defaults.
                if (GatewayProvider != Configuration.ClientConfiguration.GatewayProviderType.None) return GatewayProvider;
                if (UseAzureSystemStore) return Configuration.ClientConfiguration.GatewayProviderType.AzureTable;
                return HasStaticGateways ? Configuration.ClientConfiguration.GatewayProviderType.Config : Configuration.ClientConfiguration.GatewayProviderType.None;
            }
        }

        internal void CheckGatewayProviderSettings()
        {
            switch (GatewayProvider)
            {
                case Configuration.ClientConfiguration.GatewayProviderType.AzureTable:
                    if (!UseAzureSystemStore)
                        throw new ArgumentException("Config specifies Azure based GatewayProviderType, but Azure element is not specified or not complete.", "GatewayProvider");
                    break;
                case Configuration.ClientConfiguration.GatewayProviderType.Config:
                    if (!HasStaticGateways)
                        throw new ArgumentException("Config specifies Config based GatewayProviderType, but Gateway element(s) is/are not specified.", "GatewayProvider");
                    break;
                case Configuration.ClientConfiguration.GatewayProviderType.Custom:
                    if (String.IsNullOrEmpty(Settings.SystemStore.CustomGatewayProviderAssemblyName))
                        throw new ArgumentException("Config specifies Custom GatewayProviderType, but CustomGatewayProviderAssemblyName attribute is not specified", "GatewayProvider");
                    break;
                case Configuration.ClientConfiguration.GatewayProviderType.None:
                    if (!UseAzureSystemStore && !HasStaticGateways)
                        throw new ArgumentException("Config does not specify GatewayProviderType, and also does not have the adequate defaults: no Azure and or Gateway element(s) are specified.","GatewayProvider");
                    break;
                case Configuration.ClientConfiguration.GatewayProviderType.SqlServer:
                    if (!UseSqlSystemStore)
                        throw new ArgumentException("Config specifies SqlServer based GatewayProviderType, but DeploymentId or DataConnectionString are not specified or not complete.", "GatewayProvider");
                    break;
                case Configuration.ClientConfiguration.GatewayProviderType.ZooKeeper:
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
            var config = new ClientConfiguration {GatewayProvider = Configuration.ClientConfiguration.GatewayProviderType.Config};
            config.Gateways.Add(new IPEndPoint(IPAddress.Loopback, gatewayPort));

            return config;
        }
    }
}
