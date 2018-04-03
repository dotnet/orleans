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
using Orleans.Configuration;
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
        private const string DateFormat = "yyyy-MM-dd-HH.mm.ss.fffZ";
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="siloName">Name of this silo.</param>
        public SiloHost(string siloName)
        {
            this.Name = siloName;
            this.loggerProvider =
                new FileLoggerProvider($"SiloHost-{siloName}-{DateTime.UtcNow.ToString(DateFormat)}.log");
            this.logger = this.loggerProvider.CreateLogger(this.GetType().FullName);
            this.Type = Silo.SiloType.Secondary; // Default
            this.IsStarted = false;
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
            this.ConfigFileName = configFile.FullName;
            var config = new ClusterConfiguration();
            config.LoadFromFile(this.ConfigFileName);
            SetSiloConfig(config);
        }

        /// <summary>
        /// Initialize this silo.
        /// </summary>
        public void InitializeOrleansSilo()
        {
            try
            {
                if (!this.ConfigLoaded) LoadOrleansConfig();
                var builder = new SiloHostBuilder()
                    .Configure<SiloOptions>(options => options.SiloName = this.Name)
                    .UseConfiguration(this.Config);

                if (!string.IsNullOrWhiteSpace(this.Config.Defaults.StartupTypeName))
                {
                    builder.UseServiceProviderFactory(services =>
                        StartupBuilder.ConfigureStartup(this.Config.Defaults.StartupTypeName, services));
                }

                var host = builder.Build();

                this.orleans = host.Services.GetRequiredService<Silo>();
                var localConfig = host.Services.GetRequiredService<NodeConfiguration>();

                this.logger.Info(ErrorCode.Runtime_Error_100288, "Successfully initialized Orleans silo '{0}'.", this.orleans.Name);
            }
            catch (Exception exc)
            {
                ReportStartupError(exc);
                this.orleans = null;
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

                if (this.orleans != null)
                {
                    var shutdownEventName = this.Config.Defaults.SiloShutdownEventName ?? this.Name + "-Shutdown";

                    bool createdNew;
                    try
                    {
                        this.logger.Info(ErrorCode.SiloShutdownEventName, "Silo shutdown event name: {0}", shutdownEventName);
                        this.shutdownEvent = new EventWaitHandle(false, EventResetMode.ManualReset, shutdownEventName, out createdNew);
                        if (!createdNew)
                        {
                            this.logger.Info(ErrorCode.SiloShutdownEventOpened, "Opened existing shutdown event. Setting the event {0}", shutdownEventName);
                        }
                        else
                        {
                            this.logger.Info(ErrorCode.SiloShutdownEventCreated, "Created and set shutdown event {0}", shutdownEventName);
                        }
                    }
                    catch (PlatformNotSupportedException exc)
                    {
                        this.logger.Info(ErrorCode.SiloShutdownEventFailure, "Unable to create SiloShutdownEvent: {0}", exc.ToString());
                    }

                    // Start silo
                    this.orleans.Start();

                    // Wait for the shutdown event, and trigger a graceful shutdown if we receive it.

                    if (this.shutdownEvent != null)
                    {
                        var shutdownThread = new Thread(o =>
                        {
                            this.shutdownEvent.WaitOne();
                            this.logger.Info(ErrorCode.SiloShutdownEventReceived, "Received a shutdown event. Starting graceful shutdown.");
                            this.orleans.Shutdown();
                        })
                        {
                            IsBackground = true,
                            Name = "SiloShutdownMonitor"
                        };
                        shutdownThread.Start();
                    }

                    try
                    {
                        var startupEventName = this.Name;
                        this.logger.Info(ErrorCode.SiloStartupEventName, "Silo startup event name: {0}", startupEventName);

                        this.startupEvent = new EventWaitHandle(true, EventResetMode.ManualReset, startupEventName, out createdNew);
                        if (!createdNew)
                        {
                            this.logger.Info(ErrorCode.SiloStartupEventOpened, "Opened existing startup event. Setting the event {0}", startupEventName);
                            this.startupEvent.Set();
                        }
                        else
                        {
                            this.logger.Info(ErrorCode.SiloStartupEventCreated, "Created and set startup event {0}", startupEventName);
                        }
                    }
                    catch (PlatformNotSupportedException exc)
                    {
                        this.logger.Info(ErrorCode.SiloStartupEventFailure, "Unable to create SiloStartupEvent: {0}", exc.ToString());
                    }

                    this.logger.Info(ErrorCode.SiloStarted, "Silo {0} started successfully", this.Name);
                    this.IsStarted = true;
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
                    this.orleans = null;
                    this.IsStarted = false;
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
            this.IsStarted = false;
            if (this.orleans != null) this.orleans.Stop();
        }

        /// <summary>
        /// Gracefully shutdown this silo.
        /// </summary>
        public void ShutdownOrleansSilo()
        {
            this.IsStarted = false;
            if (this.orleans != null) this.orleans.Shutdown();
        }

        /// <summary>
        /// Returns a task that will resolve when the silo has finished shutting down, or the cancellation token is cancelled.
        /// </summary>
        /// <param name="millisecondsTimeout">Timeout, or -1 for infinite.</param>
        /// <param name="cancellationToken">Token that cancels waiting for shutdown.</param>
        /// <returns></returns>
        public Task ShutdownOrleansSiloAsync(int millisecondsTimeout, CancellationToken cancellationToken)
        {
            if (this.orleans == null || !this.IsStarted)
                return Task.CompletedTask;

            this.IsStarted = false;

            var shutdownThread = new Thread(o =>
            {
                this.orleans.Shutdown();
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
                    this.orleans.SiloTerminatedEvent,
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
            this.logger.Info(ErrorCode.SiloSetDeploymentId, "Setting Deployment Id to {0} and data connection string to {1}",
                clusterId, ConfigUtilities.RedactConnectionStringInfo(connectionString));

            this.Config.Globals.ClusterId = clusterId;
            this.Config.Globals.DataConnectionString = connectionString;
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
            this.logger.Info(ErrorCode.SiloSetSiloEndpoint, "Setting silo endpoint address to {0}:{1}", endpoint, generation);
            this.NodeConfig.HostNameOrIPAddress = endpoint.Address.ToString();
            this.NodeConfig.Port = endpoint.Port;
            this.NodeConfig.Generation = generation;
        }

        /// <summary>
        /// Set the gateway proxy endpoint address for this silo.
        /// </summary>
        /// <param name="endpoint">IP address of the gateway socket connection.</param>
        public void SetProxyEndpoint(IPEndPoint endpoint)
        {
            this.logger.Info(ErrorCode.SiloSetProxyEndpoint, "Setting silo proxy endpoint address to {0}", endpoint);
            this.NodeConfig.ProxyGatewayEndpoint = endpoint;
        }

        /// <summary>
        /// Set the seed node endpoint address to be used by silo.
        /// </summary>
        /// <param name="endpoint">IP address of the inter-silo connection socket on the seed node silo.</param>
        public void SetSeedNodeEndpoint(IPEndPoint endpoint)
        {
            this.logger.Info(ErrorCode.SiloSetSeedNode, "Adding seed node address={0} port={1}", endpoint.Address, endpoint.Port);
            this.Config.Globals.SeedNodes.Clear();
            this.Config.Globals.SeedNodes.Add(endpoint);
        }

        /// <summary>
        /// Set the set of seed node endpoint addresses to be used by silo.
        /// </summary>
        /// <param name="endpoints">IP addresses of the inter-silo connection socket on the seed node silos.</param>
        public void SetSeedNodeEndpoints(IPEndPoint[] endpoints)
        {
            // Add all silos as seed nodes
            this.Config.Globals.SeedNodes.Clear();
            foreach (IPEndPoint endpoint in endpoints)
            {
                this.logger.Info(ErrorCode.SiloAddSeedNode, "Adding seed node address={0} port={1}", endpoint.Address, endpoint.Port);
                this.Config.Globals.SeedNodes.Add(endpoint);
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
            this.logger.Info(ErrorCode.SiloSetPrimaryNode, "Setting primary node address={0} port={1}", endpoint.Address, endpoint.Port);
            this.Config.PrimaryNode = endpoint;
        }

        /// <summary>
        /// Set the type of this silo. Default is Secondary.
        /// </summary>
        /// <param name="siloType">Type of this silo.</param>
        public void SetSiloType(Silo.SiloType siloType)
        {
            this.logger.Info(ErrorCode.SiloSetSiloType, "Setting silo type {0}", siloType);
            this.Type = siloType;
        }

        /// <summary>
        ///  Set the membership liveness type to be used by this silo.
        /// </summary>
        /// <param name="livenessType">Liveness type for this silo</param>
        public void SetSiloLivenessType(GlobalConfiguration.LivenessProviderType livenessType)
        {
            this.logger.Info(ErrorCode.SetSiloLivenessType, "Setting silo Liveness Provider Type={0}", livenessType);
            this.Config.Globals.LivenessType = livenessType;
        }

        /// <summary>
        ///  Set the reminder service type to be used by this silo.
        /// </summary>
        /// <param name="reminderType">Reminder service type for this silo</param>
        public void SetReminderServiceType(GlobalConfiguration.ReminderServiceProviderType reminderType)
        {
            this.logger.Info(ErrorCode.SetSiloLivenessType, "Setting silo Reminder Service Provider Type={0}", reminderType);
            this.Config.Globals.SetReminderServiceType(reminderType);
        }

        /// <summary>
        /// Set expected deployment size.
        /// </summary>
        /// <param name="size">The expected deployment size.</param>
        public void SetExpectedClusterSize(int size)
        {
            this.logger.Info(ErrorCode.SetSiloLivenessType, "Setting Expected Cluster Size to={0}", size);
            this.Config.Globals.ExpectedClusterSize = size;
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
            if (string.IsNullOrWhiteSpace(this.Name))
                this.Name = "Silo";

            var errMsg = "ERROR starting Orleans silo name=" + this.Name + " Exception=" + LogFormatter.PrintException(exc);
            if (this.logger != null) this.logger.Error(ErrorCode.Runtime_Error_100105, errMsg, exc);

            // Dump Startup error to a log file
            var now = DateTime.UtcNow;

            var dateString = now.ToString(DateFormat, CultureInfo.InvariantCulture);
            var startupLog = this.Name + "-StartupError-" + dateString + ".txt";

            try
            {
                File.AppendAllText(startupLog, dateString + "Z" + Environment.NewLine + errMsg);
            }
            catch (Exception exc2)
            {
                if (this.logger != null) this.logger.Error(ErrorCode.Runtime_Error_100106, "Error writing log file " + startupLog, exc2);
            }
        }

        /// <summary>
        /// Search for and load the config file for this silo.
        /// </summary>
        public void LoadOrleansConfig()
        {
            if (this.ConfigLoaded) return;

            var config = this.Config ?? new ClusterConfiguration();

            try
            {
                if (this.ConfigFileName == null)
                    config.StandardLoad();
                else
                    config.LoadFromFile(this.ConfigFileName);
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
            this.Config = config;

            if (!string.IsNullOrEmpty(this.DeploymentId))
                this.Config.Globals.ClusterId = this.DeploymentId;

            if (string.IsNullOrWhiteSpace(this.Name))
                throw new ArgumentException("SiloName not defined - cannot initialize config");

            this.NodeConfig = this.Config.GetOrCreateNodeConfigurationForSilo(this.Name);
            this.Type = this.NodeConfig.IsPrimaryNode ? Silo.SiloType.Primary : Silo.SiloType.Secondary;

            this.ConfigLoaded = true;
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
            if (!this.IsStarted)
                throw new InvalidOperationException("Cannot wait for silo " + this.Name + " since it was not started successfully previously.");

            if (this.startupEvent != null)
                this.startupEvent.Reset();

            if (this.orleans != null)
            {
                // Intercept cancellation to initiate silo stop
                if (cancellationToken.HasValue)
                    cancellationToken.Value.Register(this.HandleExternalCancellation);

                this.orleans.SiloTerminatedEvent.WaitOne();
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
            this.logger.Info(ErrorCode.SiloStopping, "External cancellation triggered, starting to shutdown silo.");
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
            if (!this.disposed)
            {
                if (disposing)
                {
                    this.loggerProvider?.Dispose();
                    this.loggerProvider = null;
                    if (this.startupEvent != null)
                    {
                        this.startupEvent.Dispose();
                        this.startupEvent = null;
                    }
                    this.IsStarted = false;
                }
            }
            this.disposed = true;
        }
    }
}
