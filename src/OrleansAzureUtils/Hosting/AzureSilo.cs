/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Threading;
using Orleans.AzureUtils;
using Orleans.Runtime.Configuration;


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
        private readonly TraceLogger logger;
        private readonly IServiceRuntimeWrapper serviceRuntimeWrapper = new ServiceRuntimeWrapper();

        /// <summary>
        /// Constructor
        /// </summary>
        public AzureSilo()
        {
            DataConnectionConfigurationSettingName = AzureConstants.DataConnectionConfigurationSettingName;
            SiloEndpointConfigurationKeyName = AzureConstants.SiloEndpointConfigurationKeyName;
            ProxyEndpointConfigurationKeyName = AzureConstants.ProxyEndpointConfigurationKeyName;

            StartupRetryPause = AzureConstants.STARTUP_TIME_PAUSE; // 5 seconds
            MaxRetries = AzureConstants.MAX_RETRIES;  // 120 x 5s = Total: 10 minutes

            logger = TraceLogger.GetLogger("OrleansAzureSilo", TraceLogger.LoggerType.Runtime);
        }

        #region Azure RoleEntryPoint methods

        /// <summary>
        /// Initialize this Orleans silo for execution
        /// </summary>
        /// <returns><c>true</c> is the silo startup was successful</returns>
        public bool Start()
        {
            return Start(null);
        }

        /// <summary>
        /// Initialize this Orleans silo for execution with the specified Azure deploymentId
        /// </summary>
        /// <param name="config">If null, Config data will be read from silo config file as normal, otherwise use the specified config data.</param>
        /// <param name="deploymentId">Azure DeploymentId this silo is running under</param>
		/// <param name="connectionString">Azure DataConnectionString. If null, defaults to the DataConnectionString setting from the Azure configuration for this role.</param>
        /// <returns><c>true</c> is the silo startup was successful</returns>
        public bool Start(ClusterConfiguration config, string deploymentId = null, string connectionString = null)
        {
            // Program ident
            Trace.TraceInformation("Starting {0} v{1}", this.GetType().FullName, RuntimeVersion.Current);

            // Check if deployment id was specified
            if (deploymentId == null)
                deploymentId = serviceRuntimeWrapper.DeploymentId;

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

            myEntry = new SiloInstanceTableEntry
            {
                DeploymentId = deploymentId,
                Address = myEndpoint.Address.ToString(),
                Port = myEndpoint.Port.ToString(CultureInfo.InvariantCulture),
                Generation = generation.ToString(CultureInfo.InvariantCulture),

                HostName = host.Config.GetConfigurationForNode(host.Name).DNSHostName,
                ProxyPort = (proxyEndpoint != null ? proxyEndpoint.Port : 0).ToString(CultureInfo.InvariantCulture),

                RoleName = serviceRuntimeWrapper.RoleName, 
                InstanceName = instanceName,
                UpdateZone = serviceRuntimeWrapper.UpdateDomain.ToString(CultureInfo.InvariantCulture),
                FaultZone = serviceRuntimeWrapper.FaultDomain.ToString(CultureInfo.InvariantCulture),
                StartTime = TraceLogger.PrintDate(DateTime.UtcNow),

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
                    TraceLogger.PrintException(exc));
                Trace.TraceError(error);
                logger.Error(ErrorCode.AzureTable_34, error, exc);
                throw new OrleansException(error, exc);
            }

            // Always use Azure table for membership when running silo in Azure
            host.SetSiloLivenessType(GlobalConfiguration.LivenessProviderType.AzureTable);
            host.SetReminderServiceType(GlobalConfiguration.ReminderServiceProviderType.AzureTable);
            host.SetExpectedClusterSize(serviceRuntimeWrapper.RoleInstanceCount);
            siloInstanceManager.RegisterSiloInstance(myEntry);

            // Initialise this Orleans silo instance
            host.SetDeploymentId(deploymentId, connectionString);
            host.SetSiloEndpoint(myEndpoint, generation);
            host.SetProxyEndpoint(proxyEndpoint);

            host.InitializeOrleansSilo();
            logger.Info(ErrorCode.Runtime_Error_100288, "Successfully initialized Orleans silo '{0}' as a {1} node.", host.Name, host.Type);
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
            serviceRuntimeWrapper.UnsubscribeFromStoppingNotifcation(this, HandleAzureRoleStopping);
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
            serviceRuntimeWrapper.SubscribeForStoppingNotifcation(this, HandleAzureRoleStopping);

			if (host.IsStarted)
			{
				if (cancellationToken.HasValue)
					host.WaitForOrleansSiloShutdown(cancellationToken.Value);
				else
					host.WaitForOrleansSiloShutdown();
			}
			else
				throw new ApplicationException("Silo failed to start correctly - aborting");
		}
    }
}
