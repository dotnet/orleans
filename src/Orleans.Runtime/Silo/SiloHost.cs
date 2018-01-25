using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Threading;
using Microsoft.Extensions.Logging;
using Orleans.Logging;
using System.Threading.Tasks;
using Orleans.Runtime.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Runtime.Startup;

namespace Orleans.Runtime.Host
{
    /// <summary>
    /// Allows programmatically hosting an Orleans silo in the curent app domain.
    /// </summary>
    public class SiloHost :
        MarshalByRefObject,
        IDisposable
    {
        /// <summary> Name of this silo. </summary>
        public string Name { get; set; }

        /// <summary> Type of this silo - either <c>Primary</c> or <c>Secondary</c>. </summary>
        public Silo.SiloType Type { get; set; }

        /// <summary>
        /// Configuration file used for this silo.
        /// Changing this after the silo has started (when <c>ConfigLoaded == true</c>) will have no effect.
        /// </summary>
        public string ConfigFileName { get; set; }

        /// <summary> Configuration data for the Orleans system. </summary>
        public ClusterConfiguration Config { get; set; }

        /// <summary> Configuration data for this silo. </summary>
        public NodeConfiguration NodeConfig { get; private set; }

        /// <summary>
        /// Whether the silo config has been loaded and initializing it's runtime config.
        /// </summary>
        /// <remarks>
        /// Changes to silo config properties will be ignored after <c>ConfigLoaded == true</c>.
        /// </remarks>
        public bool ConfigLoaded { get; private set; }

        /// <summary> Cluster Id (if any) for the cluster this silo is running in. </summary>
        public string DeploymentId { get; set; }

        /// <summary> Whether this silo started successfully and is currently running. </summary>
        public bool IsStarted { get; private set; }

        private ILoggerProvider loggerProvider;
        private ILogger logger;
        private Silo orleans;
        private EventWaitHandle startupEvent;
        private EventWaitHandle shutdownEvent;
        private bool disposed;
        private const string dateFormat = "yyyy-MM-dd-HH.mm.ss.fffZ";
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="siloName">Name of this silo.</param>
        public SiloHost(string siloName)
        {
            Name = siloName;
            this.loggerProvider =
                new FileLoggerProvider($"SiloHost-{siloName}-{DateTime.UtcNow.ToString(dateFormat)}.log");
            this.logger = this.loggerProvider.CreateLogger(this.GetType().FullName);
            Type = Silo.SiloType.Secondary; // Default
            IsStarted = false;
        }

        /// <summary> Constructor </summary>
        /// <param name="siloName">Name of this silo.</param>
        /// <param name="config">Silo config that will be used to initialize this silo.</param>
        public SiloHost(string siloName, ClusterConfiguration config) : this(siloName)
        {
            SetSiloConfig(config);
        }

        /// <summary> Constructor </summary>
        /// <param name="siloName">Name of this silo.</param>
        /// <param name="configFile">Silo config file that will be used to initialize this silo.</param>
        public SiloHost(string siloName, FileInfo configFile)
            : this(siloName)
        {
            ConfigFileName = configFile.FullName;
            var config = new ClusterConfiguration();
            config.LoadFromFile(ConfigFileName);
            SetSiloConfig(config);
        }

        /// <summary>
        /// Initialize this silo.
        /// </summary>
        public void InitializeOrleansSilo()
        {
            try
            {
                if (!ConfigLoaded) LoadOrleansConfig();
                var builder = new SiloHostBuilder()
                    .ConfigureSiloName(Name)
                    .UseConfiguration(Config)
                    .ConfigureApplicationParts(parts => parts
                        .AddFromAppDomain()
                        .AddFromApplicationBaseDirectory());

                if (!string.IsNullOrWhiteSpace(Config.Defaults.StartupTypeName))
                {
                    builder.UseServiceProviderFactory(services =>
                        StartupBuilder.ConfigureStartup(Config.Defaults.StartupTypeName, services));
                }

                var host = builder.Build();

                orleans = host.Services.GetRequiredService<Silo>();
                var localConfig = host.Services.GetRequiredService<NodeConfiguration>();

                logger.Info(ErrorCode.Runtime_Error_100288, "Successfully initialized Orleans silo '{0}'.", orleans.Name);
            }
            catch (Exception exc)
            {
                ReportStartupError(exc);
                orleans = null;
            }
        }

        /// <summary>
        /// Uninitialize this silo.
        /// </summary>
        public void UnInitializeOrleansSilo()
        {
            //currently an empty method, keep this method for backward-compatibility
        }

        /// <summary>
        /// Start this silo.
        /// </summary>
        /// <returns></returns>
        public bool StartOrleansSilo(bool catchExceptions = true)
        {
            try
            {
                if (string.IsNullOrEmpty(Thread.CurrentThread.Name))
                    Thread.CurrentThread.Name = this.GetType().Name;

                if (orleans != null)
                {
                    var shutdownEventName = Config.Defaults.SiloShutdownEventName ?? Name + "-Shutdown";                    

                    bool createdNew;
                    try
                    {
                        logger.Info(ErrorCode.SiloShutdownEventName, "Silo shutdown event name: {0}", shutdownEventName);
                        shutdownEvent = new EventWaitHandle(false, EventResetMode.ManualReset, shutdownEventName, out createdNew);
                        if (!createdNew)
                        {
                            logger.Info(ErrorCode.SiloShutdownEventOpened, "Opened existing shutdown event. Setting the event {0}", shutdownEventName);
                        }
                        else
                        {
                            logger.Info(ErrorCode.SiloShutdownEventCreated, "Created and set shutdown event {0}", shutdownEventName);
                        }
                    }
                    catch (PlatformNotSupportedException exc)
                    {
                        logger.Info(ErrorCode.SiloShutdownEventFailure, "Unable to create SiloShutdownEvent: {0}", exc.ToString());
                    }

                    // Start silo
                    orleans.Start();

                    // Wait for the shutdown event, and trigger a graceful shutdown if we receive it.

                    if (shutdownEvent != null)
                    {
                        var shutdownThread = new Thread(o =>
                        {
                            shutdownEvent.WaitOne();
                            logger.Info(ErrorCode.SiloShutdownEventReceived, "Received a shutdown event. Starting graceful shutdown.");
                            orleans.Shutdown();
                        })
                        {
                            IsBackground = true,
                            Name = "SiloShutdownMonitor"
                        };
                        shutdownThread.Start(); 
                    }

                    try
                    {
                        var startupEventName = Name;
                        logger.Info(ErrorCode.SiloStartupEventName, "Silo startup event name: {0}", startupEventName);

                        startupEvent = new EventWaitHandle(true, EventResetMode.ManualReset, startupEventName, out createdNew);
                        if (!createdNew)
                        {
                            logger.Info(ErrorCode.SiloStartupEventOpened, "Opened existing startup event. Setting the event {0}", startupEventName);
                            startupEvent.Set();
                        }
                        else
                        {
                            logger.Info(ErrorCode.SiloStartupEventCreated, "Created and set startup event {0}", startupEventName);
                        }
                    }
                    catch (PlatformNotSupportedException exc)
                    {
                        logger.Info(ErrorCode.SiloStartupEventFailure, "Unable to create SiloStartupEvent: {0}", exc.ToString());
                    }

                    logger.Info(ErrorCode.SiloStarted, "Silo {0} started successfully", Name);
                    IsStarted = true;
                }
                else
                {
                    throw new InvalidOperationException("Cannot start silo " + this.Name + " due to prior initialization error");
                }
            }
            catch (Exception exc)
            {
                if (catchExceptions)
                {
                    ReportStartupError(exc);
                    orleans = null;
                    IsStarted = false;
                    return false;
                }
                else
                    throw;
            }

            return true;
        }

        /// <summary>
        /// Stop this silo.
        /// </summary>
        public void StopOrleansSilo()
        {
            IsStarted = false;
            if (orleans != null) orleans.Stop();
        }

        /// <summary>
        /// Gracefully shutdown this silo.
        /// </summary>
        public void ShutdownOrleansSilo()
        {
            IsStarted = false;
            if (orleans != null) orleans.Shutdown();
        }

        /// <summary>
        /// Returns a task that will resolve when the silo has finished shutting down, or the cancellation token is cancelled.
        /// </summary>
        /// <param name="millisecondsTimeout">Timeout, or -1 for infinite.</param>
        /// <param name="cancellationToken">Token that cancels waiting for shutdown.</param>
        /// <returns></returns>
        public Task ShutdownOrleansSiloAsync(int millisecondsTimeout, CancellationToken cancellationToken)
        {
            if (orleans == null || !IsStarted)
                return Task.CompletedTask;

            IsStarted = false;

            var shutdownThread = new Thread(o =>
            {
                orleans.Shutdown();
            })
            {
                IsBackground = true,
                Name = nameof(ShutdownOrleansSiloAsync)
            };
            shutdownThread.Start();

            return WaitForOrleansSiloShutdownAsync(millisecondsTimeout, cancellationToken);
        }

        /// <summary>
        /// /// Returns a task that will resolve when the silo has finished shutting down, or the cancellation token is cancelled.
        /// </summary>
        /// <param name="cancellationToken">Token that cancels waiting for shutdown.</param>
        /// <returns></returns>
        public Task ShutdownOrleansSiloAsync(CancellationToken cancellationToken)
        {
            return ShutdownOrleansSiloAsync(Timeout.Infinite, cancellationToken);
        }

        /// <summary>
        /// Wait for this silo to shutdown.
        /// </summary>
        /// <remarks>
        /// Note: This method call will block execution of current thread, 
        /// and will not return control back to the caller until the silo is shutdown.
        /// </remarks>
        public void WaitForOrleansSiloShutdown()
        {
            WaitForOrleansSiloShutdownImpl();
        }

        /// <summary>
        /// Wait for this silo to shutdown or to be stopped with provided cancellation token.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <remarks>
        /// Note: This method call will block execution of current thread, 
        /// and will not return control back to the caller until the silo is shutdown or 
        /// an external request for cancellation has been issued.
        /// </remarks>
        public void WaitForOrleansSiloShutdown(CancellationToken cancellationToken)
        {
            WaitForOrleansSiloShutdownImpl(cancellationToken);
        }

        /// <summary>
        /// Waits for the SiloTerminatedEvent to fire or cancellation token to be cancelled.
        /// </summary>
        /// <param name="millisecondsTimeout">Timeout, or -1 for infinite.</param>
        /// <param name="cancellationToken">Token that cancels waiting for shutdown.</param>
        /// <remarks>
        /// This is essentially an async version of WaitForOrleansSiloShutdown.
        /// </remarks>
        public async Task<bool> WaitForOrleansSiloShutdownAsync(int millisecondsTimeout, CancellationToken cancellationToken)
        {
            RegisteredWaitHandle registeredHandle = null;
            CancellationTokenRegistration tokenRegistration = default(CancellationTokenRegistration);
            try
            {
                var tcs = new TaskCompletionSource<bool>();
                registeredHandle = ThreadPool.RegisterWaitForSingleObject(
                    orleans.SiloTerminatedEvent,
                    (state, timedOut) => ((TaskCompletionSource<bool>)state).TrySetResult(!timedOut),
                    tcs,
                    millisecondsTimeout,
                    true);
                tokenRegistration = cancellationToken.Register(
                    state => ((TaskCompletionSource<bool>)state).TrySetCanceled(),
                    tcs);
                return await tcs.Task;
            }
            finally
            {
                if (registeredHandle != null)
                    registeredHandle.Unregister(null);
                tokenRegistration.Dispose();
            }
        }

        /// <summary>
        /// Set the ClusterId for this silo, 
        /// as well as the connection string to use the silo system data, 
        /// such as the cluster membership table..
        /// </summary>
        /// <param name="clusterId">ClusterId this silo is part of.</param>
        /// <param name="connectionString">Azure connection string to use the silo system data.</param>
        public void SetDeploymentId(string clusterId, string connectionString)
        {
            logger.Info(ErrorCode.SiloSetDeploymentId, "Setting Deployment Id to {0} and data connection string to {1}",
                clusterId, ConfigUtilities.RedactConnectionStringInfo(connectionString));

            Config.Globals.ClusterId = clusterId;
            Config.Globals.DataConnectionString = connectionString;
        }

        /// <summary>
        /// Set the main endpoint address for this silo,
        /// plus the silo generation value to be used to distinguish this silo instance
        /// from any previous silo instances previously running on this endpoint.
        /// </summary>
        /// <param name="endpoint">IP address and port of the main inter-silo socket connection.</param>
        /// <param name="generation">Generation number for this silo.</param>
        public void SetSiloEndpoint(IPEndPoint endpoint, int generation)
        {
            logger.Info(ErrorCode.SiloSetSiloEndpoint, "Setting silo endpoint address to {0}:{1}", endpoint, generation);
            NodeConfig.HostNameOrIPAddress = endpoint.Address.ToString();
            NodeConfig.Port = endpoint.Port;
            NodeConfig.Generation = generation;
        }

        /// <summary>
        /// Set the gateway proxy endpoint address for this silo.
        /// </summary>
        /// <param name="endpoint">IP address of the gateway socket connection.</param>
        public void SetProxyEndpoint(IPEndPoint endpoint)
        {
            logger.Info(ErrorCode.SiloSetProxyEndpoint, "Setting silo proxy endpoint address to {0}", endpoint);
            NodeConfig.ProxyGatewayEndpoint = endpoint;
        }

        /// <summary>
        /// Set the seed node endpoint address to be used by silo.
        /// </summary>
        /// <param name="endpoint">IP address of the inter-silo connection socket on the seed node silo.</param>
        public void SetSeedNodeEndpoint(IPEndPoint endpoint)
        {
            logger.Info(ErrorCode.SiloSetSeedNode, "Adding seed node address={0} port={1}", endpoint.Address, endpoint.Port);
            Config.Globals.SeedNodes.Clear();
            Config.Globals.SeedNodes.Add(endpoint);
        }

        /// <summary>
        /// Set the set of seed node endpoint addresses to be used by silo.
        /// </summary>
        /// <param name="endpoints">IP addresses of the inter-silo connection socket on the seed node silos.</param>
        public void SetSeedNodeEndpoints(IPEndPoint[] endpoints)
        {
            // Add all silos as seed nodes
            Config.Globals.SeedNodes.Clear();
            foreach (IPEndPoint endpoint in endpoints)
            {
                logger.Info(ErrorCode.SiloAddSeedNode, "Adding seed node address={0} port={1}", endpoint.Address, endpoint.Port);
                Config.Globals.SeedNodes.Add(endpoint);
            }
        }

        /// <summary>
        /// Set the endpoint addresses for the Primary silo (if any).
        /// This silo may be Primary, in which case this address should match 
        /// this silo's inter-silo connection socket address.
        /// </summary>
        /// <param name="endpoint">The IP address for the inter-silo connection socket on the Primary silo.</param>
        public void SetPrimaryNodeEndpoint(IPEndPoint endpoint)
        {
            logger.Info(ErrorCode.SiloSetPrimaryNode, "Setting primary node address={0} port={1}", endpoint.Address, endpoint.Port);
            Config.PrimaryNode = endpoint;
        }

        /// <summary>
        /// Set the type of this silo. Default is Secondary.
        /// </summary>
        /// <param name="siloType">Type of this silo.</param>
        public void SetSiloType(Silo.SiloType siloType)
        {
            logger.Info(ErrorCode.SiloSetSiloType, "Setting silo type {0}", siloType);
            Type = siloType;
        }

        /// <summary>
        ///  Set the membership liveness type to be used by this silo.
        /// </summary>
        /// <param name="livenessType">Liveness type for this silo</param>
        public void SetSiloLivenessType(GlobalConfiguration.LivenessProviderType livenessType)
        {
            logger.Info(ErrorCode.SetSiloLivenessType, "Setting silo Liveness Provider Type={0}", livenessType);
            Config.Globals.LivenessType = livenessType;
        }

        /// <summary>
        ///  Set the reminder service type to be used by this silo.
        /// </summary>
        /// <param name="reminderType">Reminder service type for this silo</param>
        public void SetReminderServiceType(GlobalConfiguration.ReminderServiceProviderType reminderType)
        {
            logger.Info(ErrorCode.SetSiloLivenessType, "Setting silo Reminder Service Provider Type={0}", reminderType);
            Config.Globals.SetReminderServiceType(reminderType);
        }

        /// <summary>
        /// Set expected deployment size.
        /// </summary>
        /// <param name="size">The expected deployment size.</param>
        public void SetExpectedClusterSize(int size)
        {
            logger.Info(ErrorCode.SetSiloLivenessType, "Setting Expected Cluster Size to={0}", size);
            Config.Globals.ExpectedClusterSize = size;
        }

        /// <summary>
        /// Report an error during silo startup.
        /// </summary>
        /// <remarks>
        /// Information on the silo startup issue will be logged to any attached Loggers,
        /// then a timestamped StartupError text file will be written to 
        /// the current working directory (if possible).
        /// </remarks>
        /// <param name="exc">Exception which caused the silo startup issue.</param>
        public void ReportStartupError(Exception exc)
        {
            if (string.IsNullOrWhiteSpace(Name))
                Name = "Silo";

            var errMsg = "ERROR starting Orleans silo name=" + Name + " Exception=" + LogFormatter.PrintException(exc);
            if (logger != null) logger.Error(ErrorCode.Runtime_Error_100105, errMsg, exc);

            // Dump Startup error to a log file
            var now = DateTime.UtcNow;
           
            var dateString = now.ToString(dateFormat, CultureInfo.InvariantCulture);
            var startupLog = Name + "-StartupError-" + dateString + ".txt";

            try
            {
                File.AppendAllText(startupLog, dateString + "Z" + Environment.NewLine + errMsg);
            }
            catch (Exception exc2)
            {
                if (logger != null) logger.Error(ErrorCode.Runtime_Error_100106, "Error writing log file " + startupLog, exc2);
            }
        }

        /// <summary>
        /// Search for and load the config file for this silo.
        /// </summary>
        public void LoadOrleansConfig()
        {
            if (ConfigLoaded) return;

            var config = Config ?? new ClusterConfiguration();

            try
            {
                if (ConfigFileName == null)
                    config.StandardLoad();
                else
                    config.LoadFromFile(ConfigFileName);
            }
            catch (Exception ex)
            {
                throw new AggregateException("Error loading Config file: " + ex.Message, ex);
            }

            SetSiloConfig(config);
        }

        /// <summary>
        /// Allows silo config to be programmatically set.
        /// </summary>
        /// <param name="config">Configuration data for this silo and cluster.</param>
        private void SetSiloConfig(ClusterConfiguration config)
        {
            Config = config;

            if (!String.IsNullOrEmpty(DeploymentId))
                Config.Globals.ClusterId = DeploymentId;

            if (string.IsNullOrWhiteSpace(Name))
                throw new ArgumentException("SiloName not defined - cannot initialize config");

            NodeConfig = Config.GetOrCreateNodeConfigurationForSilo(Name);
            Type = NodeConfig.IsPrimaryNode ? Silo.SiloType.Primary : Silo.SiloType.Secondary;

            ConfigLoaded = true;
        }

        /// <summary>
        /// Helper to wait for this silo to shutdown or to be stopped via a cancellation token.
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <remarks>
        /// Note: This method call will block execution of current thread, 
        /// and will not return control back to the caller until the silo is shutdown or 
        /// an external request for cancellation has been issued.
        /// </remarks>
        private void WaitForOrleansSiloShutdownImpl(CancellationToken? cancellationToken = null)
        {
            if (!IsStarted)
                throw new InvalidOperationException("Cannot wait for silo " + this.Name + " since it was not started successfully previously.");

            if (startupEvent != null)
                startupEvent.Reset();
            
            if (orleans != null)
            {
                // Intercept cancellation to initiate silo stop
                if (cancellationToken.HasValue)
                    cancellationToken.Value.Register(HandleExternalCancellation);

                orleans.SiloTerminatedEvent.WaitOne();
            }
            else
                throw new InvalidOperationException("Cannot wait for silo " + this.Name + " due to prior initialization error");
        }

        /// <summary>
        /// Handle the silo stop request coming from an external cancellation token.
        /// </summary>
        private void HandleExternalCancellation()
        {
            // Try to perform gracefull shutdown of Silo when we a cancellation request has been made
            logger.Info(ErrorCode.SiloStopping, "External cancellation triggered, starting to shutdown silo.");
            ShutdownOrleansSilo();
        }

        /// <summary>
        /// Called when this silo is being Disposed by .NET runtime.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary> Perform the Dispose / cleanup operation. </summary>
        /// <param name="disposing"></param>
        protected virtual void Dispose(bool disposing)
        {
            if (!disposed)
            {
                if (disposing)
                {
                    this.loggerProvider?.Dispose();
                    this.loggerProvider = null;
                    if (startupEvent != null)
                    {
                        startupEvent.Dispose();
                        startupEvent = null;
                    }
                    this.IsStarted = false;
                }
            }
            disposed = true;
        }
    }
}
