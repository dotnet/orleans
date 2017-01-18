using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Remoting.Messaging;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost.Extensions;
using Orleans.TestingHost.Utils;

namespace Orleans.TestingHost
{

    /// <summary>
    /// Important note: <see cref="TestingSiloHost"/> will be eventually deprectated. It is recommended that you use <see cref="TestCluster"/> instead.
    /// A host class for local testing with Orleans using in-process silos.
    /// 
    /// Runs a Primary and Secondary silo in seperate app domains, and client in the main app domain.
    /// Additional silos can also be started in-process if required for particular test cases.
    /// </summary>
    /// <remarks>
    /// Make sure the following files are included in any test projects that use <c>TestingSiloHost</c>, 
    /// and ensure "Copy if Newer" is set to ensure the config files are included in the test set.
    /// <code>
    /// OrleansConfigurationForTesting.xml
    /// ClientConfigurationForTesting.xml
    /// </code>
    /// Also make sure that your test project references your test grains and test grain interfaces 
    /// projects, and has CopyLocal=True set on those references [which should be the default].
    /// </remarks>
    public class TestingSiloHost
    {
        /// <summary> Single instance of TestingSiloHost </summary>
        public static TestingSiloHost Instance { get; set; }

        /// <summary> Primary silo handle </summary>
        public SiloHandle Primary { get; private set; }

        /// <summary> List of handles to the secondary silos </summary>
        public SiloHandle Secondary { get; private set; }

        private readonly List<SiloHandle> additionalSilos = new List<SiloHandle>();
        private readonly Dictionary<string, GeneratedAssembly> additionalAssemblies = new Dictionary<string, GeneratedAssembly>();

        private TestingSiloOptions siloInitOptions { get; set; }

        private TestingClientOptions clientInitOptions { get; set; }

        /// <summary> Get or set the client configuration/// </summary>
        public ClientConfiguration ClientConfig { get; private set; }

        /// <summary> Get or set the global configuration </summary>
        public GlobalConfiguration Globals { get; private set; }

        /// <summary> The deploymentId value to use in the cluster </summary>
        public string DeploymentId = null;

        /// <summary> The prefix to use in the deploymentId </summary>
        public string DeploymentIdPrefix = null;

        /// <summary> Base port number for silos in cluster </summary>
        public const int BasePort = 22222;

        /// <summary> Base port number for the gateway silos </summary>
        public const int ProxyBasePort = 40000;

        /// <summary> Number of silos in the cluster </summary>
        private static int InstanceCounter = 0;

        /// <summary> GrainFactory to use in the tests </summary>
        public IGrainFactory GrainFactory { get; private set; }

        /// <summary> GrainFactory to use in the tests </summary>
        internal IInternalGrainFactory InternalGrainFactory { get; private set; }

        /// <summary> Get the logger to use in tests </summary>
        protected Logger logger
        {
            get { return GrainClient.Logger; }
        }

        /// <summary>
        /// Start the default Primary and Secondary test silos, plus client in-process, 
        /// using the default silo config options.
        /// </summary>
        public TestingSiloHost()
            : this(false)
        {
        }

        /// <summary>
        /// Start the default Primary and Secondary test silos, plus client in-process, 
        /// ensuring that fresh silos are started if they were already running.
        /// </summary>
        public TestingSiloHost(bool startFreshOrleans)
            : this(new TestingSiloOptions { StartFreshOrleans = startFreshOrleans }, new TestingClientOptions())
        {
        }

        /// <summary>
        /// Start the default Primary and Secondary test silos, plus client in-process, 
        /// using the specified silo config options.
        /// </summary>
        public TestingSiloHost(TestingSiloOptions siloOptions)
            : this(siloOptions, new TestingClientOptions())
        {
        }

        /// <summary>
        /// Start the default Primary and Secondary test silos, plus client in-process, 
        /// using the specified silo and client config options.
        /// </summary>
        public TestingSiloHost(TestingSiloOptions siloOptions, TestingClientOptions clientOptions)
        {
            DeployTestingSiloHost(siloOptions, clientOptions);
        }

        private TestingSiloHost(string ignored)
        {
        }

        /// <summary> Create a new TestingSiloHost without initialization </summary>
        public static TestingSiloHost CreateUninitialized()
        {
            return new TestingSiloHost("Uninitialized");
        }

        private void DeployTestingSiloHost(TestingSiloOptions siloOptions, TestingClientOptions clientOptions)
        {
            siloInitOptions = siloOptions;
            clientInitOptions = clientOptions;

            AppDomain.CurrentDomain.UnhandledException += ReportUnobservedException;

            InitializeLogWriter();
            try
            {
                string startMsg = "----------------------------- STARTING NEW UNIT TEST SILO HOST: " + GetType().FullName + " -------------------------------------";
                WriteLog(startMsg);
                InitializeAsync(siloOptions, clientOptions).Wait();
                UninitializeLogWriter();
                Instance = this;
            }
            catch (TimeoutException te)
            {
                FlushLogToConsole();
                throw new TimeoutException("Timeout during test initialization", te);
            }
            catch (Exception ex)
            {
                StopAllSilos();

                Exception baseExc = ex.GetBaseException();
                FlushLogToConsole();

                if (baseExc is TimeoutException)
                {
                    throw new TimeoutException("Timeout during test initialization", ex);
                }

                // IMPORTANT:
                // Do NOT re-throw the original exception here, also not as an internal exception inside AggregateException
                // Due to the way MS tests works, if the original exception is an Orleans exception,
                // it's assembly might not be loaded yet in this phase of the test.
                // As a result, we will get "MSTest: Unit Test Adapter threw exception: Type is not resolved for member XXX"
                // and will loose the original exception. This makes debugging tests super hard!
                // The root cause has to do with us initializing our tests from Test constructor and not from TestInitialize method.
                // More details: http://dobrzanski.net/2010/09/20/mstest-unit-test-adapter-threw-exception-type-is-not-resolved-for-member/
                throw new Exception(
                    string.Format("Exception during test initialization: {0}",
                        LogFormatter.PrintException(baseExc)));
            }
        }

        /// <summary>
        /// Stop the TestingSilo and restart it.
        /// </summary>
        /// <param name="siloOptions">Cluster options to use.</param>
        /// <param name="clientOptions">Client optin to use.</param>
        public void RedeployTestingSiloHost(TestingSiloOptions siloOptions = null, TestingClientOptions clientOptions = null)
        {
            StopAllSilos();
            DeployTestingSiloHost(siloOptions ?? new TestingSiloOptions(), clientOptions ?? new TestingClientOptions());
        }

        /// <summary>
        /// Get the list of current active silos.
        /// </summary>
        /// <returns>List of current silos.</returns>
        public IEnumerable<SiloHandle> GetActiveSilos()
        {
            WriteLog("GetActiveSilos: Primary={0} Secondary={1} + {2} Additional={3}",
                Primary, Secondary, additionalSilos.Count, Orleans.Runtime.Utils.EnumerableToString(additionalSilos));

            if (null != Primary && Primary.IsActive == true) yield return Primary;
            if (null != Secondary && Secondary.IsActive == true) yield return Secondary;
            if (additionalSilos.Count > 0)
                foreach (var s in additionalSilos)
                    if (null != s && s.IsActive == true)
                        yield return s;
        }

        /// <summary>
        /// Find the silo handle for the specified silo address.
        /// </summary>
        /// <param name="siloAddress">Silo address to be found.</param>
        /// <returns>SiloHandle of the appropriate silo, or <c>null</c> if not found.</returns>
        public SiloHandle GetSiloForAddress(SiloAddress siloAddress)
        {
            List<SiloHandle> activeSilos = GetActiveSilos().ToList();
            var ret = activeSilos.Where(s => s.SiloAddress.Equals(siloAddress)).FirstOrDefault();
            return ret;
        }

        /// <summary>
        /// Wait for the silo liveness sub-system to detect and act on any recent cluster membership changes.
        /// </summary>
        /// <param name="didKill">Whether recent membership changes we done by graceful Stop.</param>
        public async Task WaitForLivenessToStabilizeAsync(bool didKill = false)
        {
            TimeSpan stabilizationTime = GetLivenessStabilizationTime(Globals, didKill);
            WriteLog(Environment.NewLine + Environment.NewLine + "WaitForLivenessToStabilize is about to sleep for {0}", stabilizationTime);
            await Task.Delay(stabilizationTime);
            WriteLog("WaitForLivenessToStabilize is done sleeping");
        }

        private static TimeSpan GetLivenessStabilizationTime(GlobalConfiguration global, bool didKill = false)
        {
            TimeSpan stabilizationTime = TimeSpan.Zero;
            if (didKill)
            {
                // in case of hard kill (kill and not Stop), we should give silos time to detect failures first.
                stabilizationTime = TestingUtils.Multiply(global.ProbeTimeout, global.NumMissedProbesLimit);
            }
            if (global.UseLivenessGossip)
            {
                stabilizationTime += TimeSpan.FromSeconds(5);
            }
            else
            {
                stabilizationTime += TestingUtils.Multiply(global.TableRefreshTimeout, 2);
            }
            return stabilizationTime;
        }

        /// <summary>
        /// Start an additional silo, so that it joins the existing cluster with the default Primary and Secondary silos.
        /// </summary>
        /// <returns>SiloHandle for the newly started silo.</returns>
        public SiloHandle StartAdditionalSilo()
        {
            SiloHandle instance = StartOrleansSilo(
                Silo.SiloType.Secondary,
                siloInitOptions,
                InstanceCounter++);
            additionalSilos.Add(instance);
            return instance;
        }

        /// <summary>
        /// Start a number of additional silo, so that they join the existing cluster with the default Primary and Secondary silos.
        /// </summary>
        /// <param name="numExtraSilos">Number of additional silos to start.</param>
        /// <returns>List of SiloHandles for the newly started silos.</returns>
        public List<SiloHandle> StartAdditionalSilos(int numExtraSilos)
        {
            List<SiloHandle> instances = new List<SiloHandle>();
            for (int i = 0; i < numExtraSilos; i++)
            {
                SiloHandle instance = StartAdditionalSilo();
                instances.Add(instance);
            }
            return instances;
        }

        /// <summary>
        /// Stop any additional silos, not including the default Primary and Secondary silos.
        /// </summary>
        public void StopAdditionalSilos()
        {
            foreach (SiloHandle instance in additionalSilos)
            {
                StopSilo(instance);
            }
            additionalSilos.Clear();
        }

        /// <summary>
        /// Restart all additional silos, not including the default Primary and Secondary silos.
        /// </summary>
        public void RestartAllAdditionalSilos()
        {
            if (additionalSilos.Count == 0) return;

            var restartedAdditionalSilos = new List<SiloHandle>();
            foreach (SiloHandle instance in additionalSilos.ToArray())
            {
                if (instance.IsActive == true)
                {
                    var restartedSilo = RestartSilo(instance);
                    restartedAdditionalSilos.Add(restartedSilo);
                }
            }
        }

        /// <summary>
        /// Stop the default Primary and Secondary silos.
        /// </summary>
        public void StopDefaultSilos()
        {
            try
            {
                GrainClient.Uninitialize();
            }
            catch (Exception exc) { WriteLog("Exception Uninitializing grain client: {0}", exc); }

            StopSilo(Secondary);
            StopSilo(Primary);
            Secondary = null;
            Primary = null;
            InstanceCounter = 0;
            DeploymentId = null;
        }

        /// <summary>
        /// Stop all current silos.
        /// </summary>
        public void StopAllSilos()
        {
            StopAdditionalSilos();
            StopDefaultSilos();
            AppDomain.CurrentDomain.UnhandledException -= ReportUnobservedException;
            Instance = null;
        }

        /// <summary>
        /// Stop all current silos if running.
        /// </summary>
        public static void StopAllSilosIfRunning()
        {
            var host = Instance;
            if (host != null)
            {
                host.StopAllSilos();
            }
        }

        /// <summary>
        /// Restart the default Primary and Secondary silos.
        /// </summary>
        public void RestartDefaultSilos(bool pickNewDeploymentId=false)
        {
            TestingSiloOptions primarySiloOptions = Primary != null ? this.siloInitOptions : null;
            TestingSiloOptions secondarySiloOptions = Secondary != null ? this.siloInitOptions : null;
            // Restart as the same deployment
            string deploymentId = DeploymentId;

            StopDefaultSilos();

            DeploymentId = pickNewDeploymentId ? null : deploymentId;
            if (primarySiloOptions != null)
            {
                primarySiloOptions.PickNewDeploymentId = pickNewDeploymentId;
                Primary = StartOrleansSilo(Silo.SiloType.Primary, primarySiloOptions, InstanceCounter++);
            }
            if (secondarySiloOptions != null)
            {
                secondarySiloOptions.PickNewDeploymentId = pickNewDeploymentId;
                Secondary = StartOrleansSilo(Silo.SiloType.Secondary, secondarySiloOptions, InstanceCounter++);
            }
            
            WaitForLivenessToStabilizeAsync().Wait();
            GrainClient.Initialize(this.ClientConfig);
        }

        /// <summary>
        /// Start a Secondary silo with a given instanceCounter 
        /// (allows to set the port number as before or new, depending on the scenario).
        /// </summary>
        public void StartSecondarySilo(TestingSiloOptions secondarySiloOptions, int instanceCounter)
        {
            secondarySiloOptions.PickNewDeploymentId = false;
            Secondary = StartOrleansSilo(Silo.SiloType.Secondary, secondarySiloOptions, instanceCounter);
        }

        /// <summary>
        /// Do a semi-graceful Stop of the specified silo.
        /// </summary>
        /// <param name="instance">Silo to be stopped.</param>
        public void StopSilo(SiloHandle instance)
        {
            if (instance != null)
            {
                StopOrleansSilo(instance, true);
            }
        }

        /// <summary>
        /// Do an immediate Kill of the specified silo.
        /// </summary>
        /// <param name="instance">Silo to be killed.</param>
        public void KillSilo(SiloHandle instance)
        {
            if (instance != null)
            {
                // do NOT stop, just kill directly, to simulate crash.
                StopOrleansSilo(instance, false);
            }
        }

        /// <summary>
        /// Performs a hard kill on client.  Client will not cleanup reasources.
        /// </summary>
        public void KillClient()
        {
            GrainClient.HardKill();
        }


        /// <summary>
        /// Do a Stop or Kill of the specified silo, followed by a restart.
        /// </summary>
        /// <param name="instance">Silo to be restarted.</param>
        public SiloHandle RestartSilo(SiloHandle instance)
        {
            if (instance != null)
            {
                var options = this.siloInitOptions;
                var type = instance.Type;
                StopOrleansSilo(instance, true);
                var newInstance = StartOrleansSilo(type, options, InstanceCounter++);

                if (Primary == instance)
                {
                    Primary = newInstance;
                }
                else if (Secondary == instance)
                {
                    Secondary = newInstance;
                }
                else
                {
                    additionalSilos.Remove(instance);
                    additionalSilos.Add(newInstance);
                }

                return newInstance;
            }
            return null;
        }

        /// <summary> Modify the cluster configurations to the test environment </summary>
        /// <param name="config">The cluster configuration to modify</param>
        /// <param name="options">the TestingSiloOptions to modify</param>
        public static void AdjustForTest(ClusterConfiguration config, TestingSiloOptions options)
        {
            if (options.AdjustConfig != null) {
                options.AdjustConfig(config);
            }

            config.AdjustForTestEnvironment(TestClusterOptions.FallbackOptions.DefaultExtendedConfiguration["DataConnectionString"]);
        }

        /// <summary> Modify the ClientConfiguration to the test environment </summary>
        /// <param name="config">The client configuration to modify</param>
        /// <param name="options">the TestingClientOptions to modify</param>
        public static void AdjustForTest(ClientConfiguration config, TestingClientOptions options)
        {
            if (options.AdjustConfig != null) {
                options.AdjustConfig(config);
            }

            config.AdjustForTestEnvironment(TestClusterOptions.FallbackOptions.DefaultExtendedConfiguration["DataConnectionString"]);
        }

        #region Private methods

        /// <summary> Initialize the grain client </summary>
        public void InitializeClient()
        {
            InitializeClient(clientInitOptions, siloInitOptions.LargeMessageWarningThreshold);
        }

        private void InitializeClient(TestingClientOptions clientOptions, int largeMessageWarningThreshold)
        {
            if (!GrainClient.IsInitialized)
            {
                WriteLog("Initializing Grain Client");
                ClientConfiguration clientConfig;

                try
                {
                    if (clientOptions.ClientConfigFile != null)
                    {
                        clientConfig = ClientConfiguration.LoadFromFile(clientOptions.ClientConfigFile.FullName);
                    }
                    else
                    {
                        clientConfig = ClientConfiguration.StandardLoad();
                    }
                }
                catch (FileNotFoundException)
                {
                    if (clientOptions.ClientConfigFile != null
                        && !string.Equals(clientOptions.ClientConfigFile.Name, TestingClientOptions.DEFAULT_CLIENT_CONFIG_FILE, StringComparison.InvariantCultureIgnoreCase))
                    {
                        // if the user is not using the defaults, then throw because the file was legitimally not found
                        throw;
                    }

                    clientConfig = ClientConfiguration.LocalhostSilo();
                }

                if (clientOptions.ProxiedGateway && clientOptions.Gateways != null)
                {
                    clientConfig.Gateways = clientOptions.Gateways;
                    if (clientOptions.PreferedGatewayIndex >= 0)
                        clientConfig.PreferedGatewayIndex = clientOptions.PreferedGatewayIndex;
                }
                if (clientOptions.PropagateActivityId)
                {
                    clientConfig.PropagateActivityId = clientOptions.PropagateActivityId;
                }
                if (!String.IsNullOrEmpty(this.DeploymentId))
                {
                    clientConfig.DeploymentId = this.DeploymentId;
                }
                if (Debugger.IsAttached)
                {
                    // Test is running inside debugger - Make timeout ~= infinite
                    clientConfig.ResponseTimeout = TimeSpan.FromMilliseconds(1000000);
                }
                else if (clientOptions.ResponseTimeout > TimeSpan.Zero)
                {
                    clientConfig.ResponseTimeout = clientOptions.ResponseTimeout;
                }

                if (largeMessageWarningThreshold > 0)
                {
                    clientConfig.LargeMessageWarningThreshold = largeMessageWarningThreshold;
                }
                AdjustForTest(clientConfig, clientOptions);
                this.ClientConfig = clientConfig;

                GrainClient.Initialize(clientConfig);
                this.GrainFactory = GrainClient.GrainFactory;
                this.InternalGrainFactory = this.GrainFactory as IInternalGrainFactory;
            }
        }

        private async Task InitializeAsync(TestingSiloOptions options, TestingClientOptions clientOptions)
        {
            bool doStartPrimary = false;
            bool doStartSecondary = false;

            if (options.StartFreshOrleans)
            {
                // the previous test was !startFresh, so we need to cleanup after it.
                StopAllSilosIfRunning();

                if (options.StartPrimary)
                {
                    doStartPrimary = true;
                }
                if (options.StartSecondary)
                {
                    doStartSecondary = true;
                }
            }
            else
            {
                var runningInstance = Instance;
                if (runningInstance != null)
                {
                    this.Primary = runningInstance.Primary;
                    this.Secondary = runningInstance.Secondary;
                    this.Globals = runningInstance.Globals;
                    this.ClientConfig = runningInstance.ClientConfig;
                    this.DeploymentId = runningInstance.DeploymentId;
                    this.DeploymentIdPrefix = runningInstance.DeploymentIdPrefix;
                    this.GrainFactory = runningInstance.GrainFactory;
                    this.additionalSilos.AddRange(runningInstance.additionalSilos);
                    foreach (var additionalAssembly in runningInstance.additionalAssemblies)
                    {
                        this.additionalAssemblies.Add(additionalAssembly.Key, additionalAssembly.Value);
                    }
                }

                if (options.StartPrimary && Primary == null)
                {
                    // first time.
                    doStartPrimary = true;
                }
                if (options.StartSecondary && Secondary == null)
                {
                    doStartSecondary = true;
                }
            }
            if (options.PickNewDeploymentId && String.IsNullOrEmpty(DeploymentId))
            {
                DeploymentId = GetDeploymentId();
            }

            if (options.ParallelStart)
            {
                var handles = new List<Task<SiloHandle>>();
                if (doStartPrimary)
                {
                    int instanceCount = InstanceCounter++;
                    handles.Add(Task.Run(() => StartOrleansSilo(Silo.SiloType.Primary, options, instanceCount)));
                }
                if (doStartSecondary)
                {
                    int instanceCount = InstanceCounter++;
                    handles.Add(Task.Run(() => StartOrleansSilo(Silo.SiloType.Secondary, options, instanceCount)));
                }
                await Task.WhenAll(handles.ToArray());
                if (doStartPrimary)
                {
                    Primary = await handles[0];
                }
                if (doStartSecondary)
                {
                    Secondary = await handles[1];
                }
            }
            else
            {
                if (doStartPrimary)
                {
                    Primary = StartOrleansSilo(Silo.SiloType.Primary, options, InstanceCounter++);
                }
                if (doStartSecondary)
                {
                    Secondary = StartOrleansSilo(Silo.SiloType.Secondary, options, InstanceCounter++);
                }
            }
            
            WriteLog("Done initializing cluster");

            if (!GrainClient.IsInitialized && options.StartClient)
            {
                InitializeClient(clientOptions, options.LargeMessageWarningThreshold);
            }
        }

        private SiloHandle StartOrleansSilo(Silo.SiloType type, TestingSiloOptions options, int instanceCount, AppDomain shared = null)
        {
            return StartOrleansSilo(this, type, options, instanceCount, shared);
        }

        /// <summary>
        /// Start a new silo in the target cluster
        /// </summary>
        /// <param name="host">The target cluster</param>
        /// <param name="type">The type of the silo to deploy</param>
        /// <param name="options">The options to use for the silo</param>
        /// <param name="instanceCount">The instance count of the silo</param>
        /// <param name="shared">The shared AppDomain to use</param>
        /// <returns>A handle to the deployed silo</returns>
        public static SiloHandle StartOrleansSilo(TestingSiloHost host, Silo.SiloType type, TestingSiloOptions options, int instanceCount, AppDomain shared = null)
        {
            if (host == null) throw new ArgumentNullException("host");

            // Load initial config settings, then apply some overrides below.
            ClusterConfiguration config = new ClusterConfiguration();
            try
            {
                if (options.SiloConfigFile == null)
                {
                    config.StandardLoad();
                }
                else
                {
                    config.LoadFromFile(options.SiloConfigFile.FullName);
                }
            }
            catch (FileNotFoundException)
            {
                if (options.SiloConfigFile != null
                    && !string.Equals(options.SiloConfigFile.Name, TestingSiloOptions.DEFAULT_SILO_CONFIG_FILE, StringComparison.InvariantCultureIgnoreCase))
                {
                    // if the user is not using the defaults, then throw because the file was legitimally not found
                    throw;
                }

                config = ClusterConfiguration.LocalhostPrimarySilo();
                config.AddMemoryStorageProvider("Default");
                config.AddMemoryStorageProvider("MemoryStore");
            }

            int basePort = options.BasePort < 0 ? BasePort : options.BasePort;


            if (config.Globals.SeedNodes.Count > 0 && options.BasePort < 0)
            {
                config.PrimaryNode = config.Globals.SeedNodes[0];
            }
            else
            {
                config.PrimaryNode = new IPEndPoint(IPAddress.Loopback, basePort);
            }
            config.Globals.SeedNodes.Clear();
            config.Globals.SeedNodes.Add(config.PrimaryNode);

            if (!String.IsNullOrEmpty(host.DeploymentId))
            {
                config.Globals.DeploymentId = host.DeploymentId;
            }

            config.Defaults.PropagateActivityId = options.PropagateActivityId;
            if (options.LargeMessageWarningThreshold > 0)
            {
                config.Defaults.LargeMessageWarningThreshold = options.LargeMessageWarningThreshold;
            }

            config.Globals.LivenessType = options.LivenessType;
            config.Globals.ReminderServiceType = options.ReminderServiceType;
            if (!String.IsNullOrEmpty(options.DataConnectionString))
            {
                config.Globals.DataConnectionString = options.DataConnectionString;
            }

            host.Globals = config.Globals;

            string siloName;
            switch (type)
            {
                case Silo.SiloType.Primary:
                    siloName = "Primary";
                    break;
                default:
                    siloName = "Secondary_" + instanceCount.ToString(CultureInfo.InvariantCulture);
                    break;
            }

            NodeConfiguration nodeConfig = config.GetOrCreateNodeConfigurationForSilo(siloName);
            nodeConfig.HostNameOrIPAddress = "loopback";
            nodeConfig.Port = basePort + instanceCount;
            nodeConfig.DefaultTraceLevel = config.Defaults.DefaultTraceLevel;
            nodeConfig.PropagateActivityId = config.Defaults.PropagateActivityId;
            nodeConfig.BulkMessageLimit = config.Defaults.BulkMessageLimit;

            int? gatewayport = null;
            if (nodeConfig.ProxyGatewayEndpoint != null && nodeConfig.ProxyGatewayEndpoint.Address != null)
            {
                gatewayport = (options.ProxyBasePort < 0 ? ProxyBasePort : options.ProxyBasePort) + instanceCount;
                nodeConfig.ProxyGatewayEndpoint = new IPEndPoint(nodeConfig.ProxyGatewayEndpoint.Address, gatewayport.Value);
            }

            config.Globals.ExpectedClusterSize = 2;

            config.Overrides[siloName] = nodeConfig;

            AdjustForTest(config, options);

            WriteLog("Starting a new silo in app domain {0} with config {1}", siloName, config.ToString(siloName));
            return AppDomainSiloHandle.Create(siloName, type, config, nodeConfig, host.additionalAssemblies);
        }

        private void StopOrleansSilo(SiloHandle instance, bool stopGracefully)
        {
            instance.StopSilo(stopGracefully);
            instance.Dispose();
        }

        private string GetDeploymentId()
        {
            if (!String.IsNullOrEmpty(DeploymentId))
            {
                return DeploymentId;
            }
            string prefix = DeploymentIdPrefix ?? "testdepid-";
            int randomSuffix = ThreadSafeRandom.Next(1000);
            DateTime now = DateTime.UtcNow;
            string DateTimeFormat = "yyyy-MM-dd-hh-mm-ss-fff";
            string depId = String.Format("{0}{1}-{2}",
                prefix, now.ToString(DateTimeFormat, CultureInfo.InvariantCulture), randomSuffix);
            return depId;
        }

        #endregion

        #region Tracing helper functions. Will eventually go away entirely

        private const string LogWriterContextKey = "TestingSiloHost_LogWriter";

        private static void ReportUnobservedException(object sender, UnhandledExceptionEventArgs eventArgs)
        {
            Exception exception = (Exception)eventArgs.ExceptionObject;
            WriteLog("Unobserved exception: {0}", exception);
        }

        private static void WriteLog(string format, params object[] args)
        {
            var trace = CallContext.LogicalGetData(LogWriterContextKey);
            if (trace != null)
            {
                CallContext.LogicalSetData(LogWriterContextKey, string.Format(format, args) + Environment.NewLine);
            }
        }

        private static void FlushLogToConsole()
        {
            var trace = CallContext.LogicalGetData(LogWriterContextKey);
            if (trace != null)
            {
                Console.WriteLine(trace);
                UninitializeLogWriter();
            }
        }

        private static void WriteLog(object value)
        {
            WriteLog(value.ToString());
        }

        private static void InitializeLogWriter()
        {
            CallContext.LogicalSetData(LogWriterContextKey, string.Empty);
        }

        private static void UninitializeLogWriter()
        {
            CallContext.FreeNamedDataSlot(LogWriterContextKey);
        }

        #endregion
    }
}
