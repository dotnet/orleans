using System;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Threading;
using Orleans.AzureUtils;
using Orleans.Runtime.Configuration;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Logging;
using Orleans.AzureUtils.Utilities;
using Orleans.Configuration;
using Orleans.Hosting.AzureCloudServices;
using Orleans.Hosting;

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

        /// <summary>delegate to add some configuration to the client</summary>
        public Action<ISiloHostBuilder> ConfigureSiloHostDelegate { get; set; }

        private SiloHost host;
        private readonly ILogger logger;
        private readonly IServiceRuntimeWrapper serviceRuntimeWrapper;
        //TODO: hook this up with SiloBuilder when SiloBuilder supports create AzureSilo
        private static ILoggerFactory DefaultLoggerFactory = CreateDefaultLoggerFactory("AzureSilo.log");

        private readonly ILoggerFactory loggerFactory = DefaultLoggerFactory;

        public AzureSilo()
            :this(new ServiceRuntimeWrapper(DefaultLoggerFactory), DefaultLoggerFactory)
        {
        }

        /// <summary>
        /// Constructor
        /// </summary>
        public AzureSilo(ILoggerFactory loggerFactory)
            : this(new ServiceRuntimeWrapper(loggerFactory), loggerFactory)
        {
        }

        public static ILoggerFactory CreateDefaultLoggerFactory(string filePath)
        {
            var factory = new LoggerFactory();
            factory.AddProvider(new FileLoggerProvider(filePath));
            if (ConsoleText.IsConsoleAvailable)
                factory.AddConsole();
            return factory;
        }

        internal AzureSilo(IServiceRuntimeWrapper serviceRuntimeWrapper, ILoggerFactory loggerFactory)
        {
            this.serviceRuntimeWrapper = serviceRuntimeWrapper;
            DataConnectionConfigurationSettingName = AzureConstants.DataConnectionConfigurationSettingName;
            SiloEndpointConfigurationKeyName = AzureConstants.SiloEndpointConfigurationKeyName;
            ProxyEndpointConfigurationKeyName = AzureConstants.ProxyEndpointConfigurationKeyName;

            StartupRetryPause = AzureConstants.STARTUP_TIME_PAUSE; // 5 seconds
            MaxRetries = AzureConstants.MAX_RETRIES;  // 120 x 5s = Total: 10 minutes

            this.loggerFactory = loggerFactory;
            logger = loggerFactory.CreateLogger<AzureSilo>();
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
                string clusterId = config.Globals.ClusterId ?? serviceRuntimeWrapper.DeploymentId;
                string connectionString = config.Globals.DataConnectionString ??
                                          serviceRuntimeWrapper.GetConfigurationSettingValue(DataConnectionConfigurationSettingName);

                try
                {
                    var manager = await OrleansSiloInstanceManager.GetManager(clusterId, connectionString, AzureStorageClusteringOptions.DEFAULT_TABLE_NAME, loggerFactory);
                    var instances = await manager.DumpSiloInstanceTable();
                    logger.Debug(instances);
                }
                catch (Exception exc)
                {
                    var error = String.Format("Connecting to the storage table has failed with {0}", LogFormatter.PrintException(exc));
                    Trace.TraceError(error);
                    logger.Error((int)AzureSiloErrorCode.AzureTable_34, error, exc);
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
            return DefaultConfiguration(new ServiceRuntimeWrapper(DefaultLoggerFactory));
        }

        internal static ClusterConfiguration DefaultConfiguration(IServiceRuntimeWrapper serviceRuntimeWrapper)
        {
            var config = new ClusterConfiguration();

            config.Globals.LivenessType = GlobalConfiguration.LivenessProviderType.AzureTable;
            config.Globals.ClusterId = serviceRuntimeWrapper.DeploymentId;
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

        /// <summary>
        /// Initialize this Orleans silo for execution. Config data will be read from silo config file as normal
        /// </summary>
        /// <param name="deploymentId">Azure ClusterId this silo is running under. If null, defaults to the value from the configuration.</param>
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
        /// Initialize this Orleans silo for execution with the specified Azure clusterId
        /// </summary>
        /// <param name="config">If null, Config data will be read from silo config file as normal, otherwise use the specified config data.</param>
        /// <param name="clusterId">Azure ClusterId this silo is running under</param>
		/// <param name="connectionString">Azure DataConnectionString. If null, defaults to the DataConnectionString setting from the Azure configuration for this role.</param>
        /// <returns><c>true</c> if the silo startup was successful</returns>
        internal bool Start(ClusterConfiguration config, string clusterId, string connectionString)
        {
            if (config != null && clusterId != null)
                throw new ArgumentException("Cannot use config and clusterId on the same time");

            // Program ident
            Trace.TraceInformation("Starting {0} v{1}", this.GetType().FullName, RuntimeVersion.Current);

            // Read endpoint info for this instance from Azure config
            string instanceName = serviceRuntimeWrapper.InstanceName;

            // Configure this Orleans silo instance
            if (config == null)
            {
                host = new SiloHost(instanceName);
                host.LoadConfig(); // Load config from file + Initializes logger configurations
            }
            else
            {
                host = new SiloHost(instanceName, config); // Use supplied config data + Initializes logger configurations
            }

            IPEndPoint myEndpoint = serviceRuntimeWrapper.GetIPEndpoint(SiloEndpointConfigurationKeyName);
            IPEndPoint proxyEndpoint = serviceRuntimeWrapper.GetIPEndpoint(ProxyEndpointConfigurationKeyName);

            host.SetSiloType(Silo.SiloType.Secondary);

            // If clusterId was not direclty provided, take the value in the config. If it is not 
            // in the config too, just take the ClusterId from Azure
            if (clusterId == null)
                clusterId = string.IsNullOrWhiteSpace(host.Config.Globals.ClusterId)
                    ? serviceRuntimeWrapper.DeploymentId
                    : host.Config.Globals.ClusterId;

            if (connectionString == null)
                connectionString = serviceRuntimeWrapper.GetConfigurationSettingValue(DataConnectionConfigurationSettingName);

            // Always use Azure table for membership when running silo in Azure
            host.SetSiloLivenessType(GlobalConfiguration.LivenessProviderType.AzureTable);
            if (host.Config.Globals.ReminderServiceType == GlobalConfiguration.ReminderServiceProviderType.NotSpecified ||
                host.Config.Globals.ReminderServiceType == GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain)
            {
                host.SetReminderServiceType(GlobalConfiguration.ReminderServiceProviderType.AzureTable);
            }
            host.SetExpectedClusterSize(serviceRuntimeWrapper.RoleInstanceCount);

            // Initialize this Orleans silo instance
            host.SetDeploymentId(clusterId, connectionString);
            host.SetSiloEndpoint(myEndpoint, 0);
            host.SetProxyEndpoint(proxyEndpoint);

            host.ConfigureSiloHostDelegate = ConfigureSiloHostDelegate;

            host.InitializeSilo();
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
            host.ShutdownSilo();
            logger.Info(ErrorCode.Runtime_Error_100291, "Orleans silo '{0}' shutdown.", host.Name);
        }

        private bool StartSilo()
        {
            logger.Info(ErrorCode.Runtime_Error_100292, "Starting Orleans silo '{0}' as a {1} node.", host.Name, host.Type);

            bool ok = host.StartSilo();

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
            host.ShutdownSilo();
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
					host.WaitForSiloShutdown(cancellationToken.Value);
				else
					host.WaitForSiloShutdown();
			}
			else
				throw new Exception("Silo failed to start correctly - aborting");
		}
    }
}
