using System;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Threading;
using Orleans.AzureUtils;
using Orleans.Runtime.Configuration;
using System.Threading.Tasks;

namespace Orleans.Runtime.Host
{
    /// <summary>
    /// Wrapper class for an Orleans silo running in the current host process.
    /// </summary>
    public class AzureSilo
    {
        /// <summary>
        /// Amount of time to pause before retrying if a secondary silo is unable to connect to the primary silo for this deployment.
        /// Defaults to 5 seconds.
        /// </summary>
        public TimeSpan StartupRetryPause { get; set; }
        /// <summary>
        /// Number of times to retrying if a secondary silo is unable to connect to the primary silo for this deployment.
        /// Defaults to 120 times.
        /// </summary>
        public int MaxRetries { get; set; }
        
        /// <summary>
        /// The name of the configuration key value for locating the DataConnectionString setting from the Azure configuration for this role.
        /// Defaults to <c>DataConnectionString</c>
        /// </summary>
        public string DataConnectionConfigurationSettingName { get; set; }
        /// <summary>
        /// The name of the configuration key value for locating the OrleansSiloEndpoint setting from the Azure configuration for this role.
        /// Defaults to <c>OrleansSiloEndpoint</c>
        /// </summary>
        public string SiloEndpointConfigurationKeyName { get; set; }
        /// <summary>
        /// The name of the configuration key value for locating the OrleansProxyEndpoint setting from the Azure configuration for this role.
        /// Defaults to <c>OrleansProxyEndpoint</c>
        /// </summary>
        public string ProxyEndpointConfigurationKeyName { get; set; }
        
        private SiloHost host;
        private OrleansSiloInstanceManager siloInstanceManager;
        private SiloInstanceTableEntry myEntry;
        private readonly Logger logger;
        private readonly IServiceRuntimeWrapper serviceRuntimeWrapper;

        /// <summary>
        /// Constructor
        /// </summary>
        public AzureSilo()
            : this(new ServiceRuntimeWrapper())
        {
        }

        internal AzureSilo(IServiceRuntimeWrapper serviceRuntimeWrapper)
        {
            this.serviceRuntimeWrapper = serviceRuntimeWrapper;
            DataConnectionConfigurationSettingName = AzureConstants.DataConnectionConfigurationSettingName;
            SiloEndpointConfigurationKeyName = AzureConstants.SiloEndpointConfigurationKeyName;
            ProxyEndpointConfigurationKeyName = AzureConstants.ProxyEndpointConfigurationKeyName;

            StartupRetryPause = AzureConstants.STARTUP_TIME_PAUSE; // 5 seconds
            MaxRetries = AzureConstants.MAX_RETRIES;  // 120 x 5s = Total: 10 minutes

            logger = LogManager.GetLogger("OrleansAzureSilo", LoggerType.Runtime);
        }

        /// <summary>
        /// Async method to validate specific cluster configuration
        /// </summary>
        /// <param name="config"></param>
        /// <returns>Task object of boolean type for this async method </returns>
        public async Task<bool> ValidateConfiguration(ClusterConfiguration config)
        {
            if (config.Globals.LivenessType == GlobalConfiguration.LivenessProviderType.AzureTable)
            {
                string deploymentId = config.Globals.DeploymentId ?? serviceRuntimeWrapper.DeploymentId;
                string connectionString = config.Globals.DataConnectionString ??
                                          serviceRuntimeWrapper.GetConfigurationSettingValue(DataConnectionConfigurationSettingName);

                try
                {
                    var manager = siloInstanceManager ?? await OrleansSiloInstanceManager.GetManager(deploymentId, connectionString);
                    var instances = await manager.DumpSiloInstanceTable();
                    logger.Verbose(instances);
                }
                catch (Exception exc)
                {
                    var error = String.Format("Connecting to the storage table has failed with {0}", LogFormatter.PrintException(exc));
                    Trace.TraceError(error);
                    logger.Error(ErrorCode.AzureTable_34, error, exc);
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Default cluster configuration
        /// </summary>
        /// <returns>Default ClusterConfiguration </returns>
        public static ClusterConfiguration DefaultConfiguration()
        {
            return DefaultConfiguration(new ServiceRuntimeWrapper());
        }

        internal static ClusterConfiguration DefaultConfiguration(IServiceRuntimeWrapper serviceRuntimeWrapper)
        {
            var config = new ClusterConfiguration();

            config.Globals.LivenessType = GlobalConfiguration.LivenessProviderType.AzureTable;
            config.Globals.DeploymentId = serviceRuntimeWrapper.DeploymentId;
            try
            {
                config.Globals.DataConnectionString = serviceRuntimeWrapper.GetConfigurationSettingValue(AzureConstants.DataConnectionConfigurationSettingName);
            }
            catch (Exception exc)
            {
                if (exc.GetType().Name.Contains("RoleEnvironmentException"))
                {
                    config.Globals.DataConnectionString = null;
                }
                else
                {
                    throw;
                }
            }
            
            return config;
        }

        #region Azure RoleEntryPoint methods

        /// <summary>
        /// Initialize this Orleans silo for execution. Config data will be read from silo config file as normal
        /// </summary>
        /// <param name="deploymentId">Azure DeploymentId this silo is running under. If null, defaults to the value from the configuration.</param>
		/// <param name="connectionString">Azure DataConnectionString. If null, defaults to the DataConnectionString setting from the Azure configuration for this role.</param>
        /// <returns><c>true</c> is the silo startup was successful</returns>
        public bool Start(string deploymentId = null, string connectionString = null)
        {
            return Start(null, deploymentId, connectionString);
        }

        /// <summary>
        /// Initialize this Orleans silo for execution
        /// </summary>
        /// <param name="config">Use the specified config data.</param>
		/// <param name="connectionString">Azure DataConnectionString. If null, defaults to the DataConnectionString setting from the Azure configuration for this role.</param>
        /// <returns><c>true</c> is the silo startup was successful</returns>
        public bool Start(ClusterConfiguration config, string connectionString = null)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            return Start(config, null, connectionString);
        }

        /// <summary>
        /// Initialize this Orleans silo for execution with the specified Azure deploymentId
        /// </summary>
        /// <param name="config">If null, Config data will be read from silo config file as normal, otherwise use the specified config data.</param>
        /// <param name="deploymentId">Azure DeploymentId this silo is running under</param>
		/// <param name="connectionString">Azure DataConnectionString. If null, defaults to the DataConnectionString setting from the Azure configuration for this role.</param>
        /// <returns><c>true</c> if the silo startup was successful</returns>
        internal bool Start(ClusterConfiguration config, string deploymentId, string connectionString)
        {
            if (config != null && deploymentId != null)
                throw new ArgumentException("Cannot use config and deploymentId on the same time");

            // Program ident
            Trace.TraceInformation("Starting {0} v{1}", this.GetType().FullName, RuntimeVersion.Current);

            // Read endpoint info for this instance from Azure config
            string instanceName = serviceRuntimeWrapper.InstanceName;

            // Configure this Orleans silo instance
            if (config == null)
            {
                host = new SiloHost(instanceName);
                host.LoadOrleansConfig(); // Load config from file + Initializes logger configurations
            }
            else
            {
                host = new SiloHost(instanceName, config); // Use supplied config data + Initializes logger configurations
            }

            IPEndPoint myEndpoint = serviceRuntimeWrapper.GetIPEndpoint(SiloEndpointConfigurationKeyName);
            IPEndPoint proxyEndpoint = serviceRuntimeWrapper.GetIPEndpoint(ProxyEndpointConfigurationKeyName);

            host.SetSiloType(Silo.SiloType.Secondary);

            int generation = SiloAddress.AllocateNewGeneration();

            // Bootstrap this Orleans silo instance

            // If deploymentId was not direclty provided, take the value in the config. If it is not 
            // in the config too, just take the DeploymentId from Azure
            if (deploymentId == null)
                deploymentId = string.IsNullOrWhiteSpace(host.Config.Globals.DeploymentId)
                    ? serviceRuntimeWrapper.DeploymentId
                    : host.Config.Globals.DeploymentId;

            myEntry = new SiloInstanceTableEntry
            {
                DeploymentId = deploymentId,
                Address = myEndpoint.Address.ToString(),
                Port = myEndpoint.Port.ToString(CultureInfo.InvariantCulture),
                Generation = generation.ToString(CultureInfo.InvariantCulture),

                HostName = host.Config.GetOrCreateNodeConfigurationForSilo(host.Name).DNSHostName,
                ProxyPort = (proxyEndpoint != null ? proxyEndpoint.Port : 0).ToString(CultureInfo.InvariantCulture),

                RoleName = serviceRuntimeWrapper.RoleName,
                SiloName = instanceName,
                UpdateZone = serviceRuntimeWrapper.UpdateDomain.ToString(CultureInfo.InvariantCulture),
                FaultZone = serviceRuntimeWrapper.FaultDomain.ToString(CultureInfo.InvariantCulture),
                StartTime = LogFormatter.PrintDate(DateTime.UtcNow),

                PartitionKey = deploymentId,
                RowKey = myEndpoint.Address + "-" + myEndpoint.Port + "-" + generation
            };

			if (connectionString == null)
				connectionString = serviceRuntimeWrapper.GetConfigurationSettingValue(DataConnectionConfigurationSettingName);

            try
            {
                siloInstanceManager = OrleansSiloInstanceManager.GetManager(
                    deploymentId, connectionString).WithTimeout(AzureTableDefaultPolicies.TableCreationTimeout).Result;
            }
            catch (Exception exc)
            {
                var error = String.Format("Failed to create OrleansSiloInstanceManager. This means CreateTableIfNotExist for silo instance table has failed with {0}",
                    LogFormatter.PrintException(exc));
                Trace.TraceError(error);
                logger.Error(ErrorCode.AzureTable_34, error, exc);
                throw new OrleansException(error, exc);
            }

            // Always use Azure table for membership when running silo in Azure
            host.SetSiloLivenessType(GlobalConfiguration.LivenessProviderType.AzureTable);
            if (host.Config.Globals.ReminderServiceType == GlobalConfiguration.ReminderServiceProviderType.NotSpecified ||
                host.Config.Globals.ReminderServiceType == GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain)
            {
                host.SetReminderServiceType(GlobalConfiguration.ReminderServiceProviderType.AzureTable);
            }
            host.SetExpectedClusterSize(serviceRuntimeWrapper.RoleInstanceCount);
            siloInstanceManager.RegisterSiloInstance(myEntry);

            // Initialize this Orleans silo instance
            host.SetDeploymentId(deploymentId, connectionString);
            host.SetSiloEndpoint(myEndpoint, generation);
            host.SetProxyEndpoint(proxyEndpoint);

            host.InitializeOrleansSilo();
            return StartSilo();
        }

        /// <summary>
        /// Makes this Orleans silo begin executing and become active.
        /// Note: This method call will only return control back to the caller when the silo is shutdown.
        /// </summary>
		public void Run()
        {
            RunImpl();
        }

		/// <summary>
		/// Makes this Orleans silo begin executing and become active.
		/// Note: This method call will only return control back to the caller when the silo is shutdown or 
		/// an external request for cancellation has been issued.
		/// </summary>
		/// <param name="cancellationToken">Cancellation token.</param>
		public void Run(CancellationToken cancellationToken)
		{
			RunImpl(cancellationToken);
		}

        /// <summary>
        /// Stop this Orleans silo executing.
        /// </summary>
        public void Stop()
        {
            logger.Info(ErrorCode.Runtime_Error_100290, "Stopping {0}", this.GetType().FullName);
            serviceRuntimeWrapper.UnsubscribeFromStoppingNotification(this, HandleAzureRoleStopping);
            host.ShutdownOrleansSilo();
            logger.Info(ErrorCode.Runtime_Error_100291, "Orleans silo '{0}' shutdown.", host.Name);
        }

        #endregion

        private bool StartSilo()
        {
            logger.Info(ErrorCode.Runtime_Error_100292, "Starting Orleans silo '{0}' as a {1} node.", host.Name, host.Type);

            bool ok = host.StartOrleansSilo();

            if (ok)
                logger.Info(ErrorCode.Runtime_Error_100293, "Successfully started Orleans silo '{0}' as a {1} node.", host.Name, host.Type);
            else
                logger.Error(ErrorCode.Runtime_Error_100285, string.Format("Failed to start Orleans silo '{0}' as a {1} node.", host.Name, host.Type));
            
            return ok;
        }

        private void HandleAzureRoleStopping(object sender, object e)
        {
            // Try to perform gracefull shutdown of Silo when we detect Azure role instance is being stopped
            logger.Info(ErrorCode.SiloStopping, "HandleAzureRoleStopping - starting to shutdown silo");
            host.ShutdownOrleansSilo();
        }

		/// <summary>
		/// Run method helper.
		/// </summary>
		/// <remarks>
		/// Makes this Orleans silo begin executing and become active.
		/// Note: This method call will only return control back to the caller when the silo is shutdown or 
		/// an external request for cancellation has been issued.
		/// </remarks>
		/// <param name="cancellationToken">Optional cancellation token.</param>
		private void RunImpl(CancellationToken? cancellationToken = null)
		{
			logger.Info(ErrorCode.Runtime_Error_100289, "OrleansAzureHost entry point called");

			// Hook up to receive notification of Azure role stopping events
            serviceRuntimeWrapper.SubscribeForStoppingNotification(this, HandleAzureRoleStopping);

			if (host.IsStarted)
			{
				if (cancellationToken.HasValue)
					host.WaitForOrleansSiloShutdown(cancellationToken.Value);
				else
					host.WaitForOrleansSiloShutdown();
			}
			else
				throw new Exception("Silo failed to start correctly - aborting");
		}
    }
}
