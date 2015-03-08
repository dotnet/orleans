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

﻿using System;
using System.IO;
using System.Net;
using System.Runtime;
using System.Threading;
using System.Globalization;

using Orleans.Runtime.Configuration;


namespace Orleans.Runtime.Host
{
    /// <summary>
    /// Allows programmatically hosting an Orleans silo in the curent app domain.
    /// </summary>
    public class SiloHost : MarshalByRefObject, IDisposable
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

        /// <summary>
        /// Directory to use for the trace log file written by this silo.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The values of <c>null</c> or <c>"None"</c> mean no log file will be written by Orleans Logger manager.
        /// </para>
        /// <para>
        /// When deciding The values of <c>null</c> or <c>"None"</c> mean no log file will be written by Orleans Logger manager.
        /// </para>
        /// </remarks>
        public string TraceFilePath { get; set; }

        /// <summary> Configuration data for the Orleans system. </summary>
        public ClusterConfiguration Config { get; set; }

        /// <summary> Configuration data for this silo. </summary>
        public NodeConfiguration NodeConfig { get; private set; }

        /// <summary> 
        /// Silo Debug flag. 
        /// If set to <c>true</c> then additional diagnostic info will be written during silo startup.
        ///  </summary>
        public bool Debug { get; set; }

        /// <summary>
        /// Whether the silo config has been loaded and initializing it's runtime config.
        /// </summary>
        /// <remarks>
        /// Changes to silo config properties will be ignored after <c>ConfigLoaded == true</c>.
        /// </remarks>
        public bool ConfigLoaded { get; private set; }

        /// <summary> Deployment Id (if any) for the cluster this silo is running in. </summary>
        public string DeploymentId { get; set; }

        /// <summary>
        /// Verbose flag. 
        /// If set to <c>true</c> then additional status and diagnostics info will be written during silo startup.
        /// </summary>
        public int Verbose { get; set; }

        /// <summary> Whether this silo started successfully and is currently running. </summary>
        public bool IsStarted { get; private set; }

        private TraceLogger logger;
        private Silo orleans;
        private EventWaitHandle startupEvent;
        private bool disposed;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="siloName">Name of this silo.</param>
        public SiloHost(string siloName)
        {
            Name = siloName;
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
    #if DEBUG
            AssemblyLoaderUtils.EnableAssemblyLoadTracing();
    #endif

            try
            {
                if (!ConfigLoaded) LoadOrleansConfig();

                logger.Info( ErrorCode.SiloInitializing, "Initializing Silo {0} on host={1} CPU count={2} running .NET version='{3}' Is .NET 4.5={4} OS version='{5}'",
                    Name, Environment.MachineName, Environment.ProcessorCount, Environment.Version, ConfigUtilities.IsNet45OrNewer(), Environment.OSVersion);

                logger.Info(ErrorCode.SiloGcSetting, "Silo running with GC settings: ServerGC={0} GCLatencyMode={1}", GCSettings.IsServerGC, Enum.GetName(typeof(GCLatencyMode), GCSettings.LatencyMode));
                if (!GCSettings.IsServerGC)
                    logger.Warn(ErrorCode.SiloGcWarning, "Note: Silo not running with ServerGC turned on - recommend checking app config : <configuration>-<runtime>-<gcServer enabled=\"true\"> and <configuration>-<runtime>-<gcConcurrent enabled=\"false\"/>");
                
                orleans = new Silo(Name, Type, Config);
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
            Utils.SafeExecute(UnobservedExceptionsHandlerClass.ResetUnobservedExceptionHandler);
            Utils.SafeExecute(TraceLogger.UnInitialize);
        }

        /// <summary>
        /// Start this silo.
        /// </summary>
        /// <returns></returns>
        public bool StartOrleansSilo()
        {
            try
            {
                if (string.IsNullOrEmpty(Thread.CurrentThread.Name))
                    Thread.CurrentThread.Name = this.GetType().Name;
                
                if (orleans != null)
                {
                    orleans.Start();
                    
                    var startupEventName = Name;
                    logger.Info(ErrorCode.SiloStartupEventName, "Silo startup event name: {0}", startupEventName);

                    bool createdNew;
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
                ReportStartupError(exc);
                orleans = null;
                IsStarted = false;
                return false;
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
        /// Wait for this silo to shutdown.
        /// </summary>
        /// <remarks>
        /// Note: This method call will block execution of current thread, 
        /// and will not return control back to the caller until the silo is shutdown.
        /// </remarks>
        public void WaitForOrleansSiloShutdown()
        {
            if (!IsStarted)
                throw new InvalidOperationException("Cannot wait for silo " + this.Name + " since it was not started successfully previously.");
            
            if (startupEvent != null)
                startupEvent.Reset();
            else
                throw new InvalidOperationException("Cannot wait for silo " + this.Name + " due to prior initialization error");
            
            if (orleans != null)
                orleans.SiloTerminatedEvent.WaitOne();
            else
                throw new InvalidOperationException("Cannot wait for silo " + this.Name + " due to prior initialization error");
        }

        /// <summary>
        /// Set the DeploymentId for this silo, 
        /// as well as the Azure connection string to use the silo system data, 
        /// such as the cluster membership table..
        /// </summary>
        /// <param name="deploymentId">DeploymentId this silo is part of.</param>
        /// <param name="connectionString">Azure connection string to use the silo system data.</param>
        public void SetDeploymentId(string deploymentId, string connectionString)
        {
            logger.Info(ErrorCode.SiloSetDeploymentId, "Setting Deployment Id to {0} and data connection string to {1}", 
                deploymentId, ConfigUtilities.RedactConnectionStringInfo(connectionString));

            Config.Globals.DeploymentId = deploymentId;
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

            var errMsg = "ERROR starting Orleans silo name=" + Name + " Exception=" + TraceLogger.PrintException(exc);
            if (logger != null) logger.Error(ErrorCode.Runtime_Error_100105, errMsg, exc);

            // Dump Startup error to a log file
            var now = DateTime.UtcNow;
            const string dateFormat = "yyyy-MM-dd-HH.mm.ss.fffZ";
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

            TraceLogger.Flush();
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
        /// <param name="config">Configuration data for this silo & cluster.</param>
        private void SetSiloConfig(ClusterConfiguration config)
        {
            Config = config;

            if (Verbose > 0)
                Config.Defaults.DefaultTraceLevel = (Logger.Severity.Verbose - 1 + Verbose);
            

            if (!String.IsNullOrEmpty(DeploymentId))
                Config.Globals.DeploymentId = DeploymentId;
            
            if (string.IsNullOrWhiteSpace(Name))
                throw new ArgumentException("SiloName not defined - cannot initialize config");

            NodeConfig = Config.GetConfigurationForNode(Name);
            Type = NodeConfig.IsPrimaryNode ? Silo.SiloType.Primary : Silo.SiloType.Secondary;

            if (TraceFilePath != null)
            {
                var traceFileName = Config.GetConfigurationForNode(Name).TraceFileName;
                if (traceFileName != null && !Path.IsPathRooted(traceFileName))
                    Config.GetConfigurationForNode(Name).TraceFileName = TraceFilePath + "\\" + traceFileName;
            }

            ConfigLoaded = true;
            InitializeLogger(config.GetConfigurationForNode(Name));
        }

        private void InitializeLogger(NodeConfiguration nodeCfg)
        {
            TraceLogger.Initialize(nodeCfg);
            logger = TraceLogger.GetLogger("OrleansSiloHost", TraceLogger.LoggerType.Runtime);
        }

        /// <summary>
        /// Called when this silo is being Disposed by .NET runtime.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposed)
            {
                if (disposing)
                {
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
