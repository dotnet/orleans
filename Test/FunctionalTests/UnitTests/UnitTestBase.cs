using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Net;
using System.Reflection;
using System.Runtime.Remoting;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.AzureUtils;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Scheduler;
using Orleans.Serialization;

namespace UnitTests
{
    [Serializable]
    public class SiloHandle
    {
        public Silo Silo { get; set; }
        public AppDomain AppDomain { get; set; }
        public Options Options { get; set; }
        public bool IsInProcess { get; set; }
        public string Name { get; set; }
        public Process Process { get; set; }
        public string MachineName { get; set; }
        private IPEndPoint endpoint;
        public IPEndPoint Endpoint
        {
            get
            {
                // watch it! In OutOfProcess case the IPEndPoint may not be correct, 
                // as the port is sometimes allocated inside the silo, so this endpoint variable will have a zero port.
                return endpoint;
            }

            set
            {
                endpoint = value;
            }
        }
        public override string ToString()
        {
            return String.Format("SiloHandle:{0}", Endpoint);
        }
    }

    [DeploymentItem("OrleansConfiguration.xml")]
    [DeploymentItem("ClientConfiguration.xml")]
    public class UnitTestBase
    {
        protected static AppDomain SharedMemoryDomain;
        internal static SiloHandle Primary = null;
        internal static SiloHandle Secondary = null;
        private static readonly List<SiloHandle> additionalSilos = new List<SiloHandle>();
        protected static bool cleanedFileStore = false;
        protected bool startFresh;
        public TraceLogger logger;

        private readonly Options SiloOptions;
        private readonly ClientOptions ClientOptions;
        protected static GlobalConfiguration Globals { get; set; }
        protected static ClientConfiguration ClientConfig { get; set; }
        protected static string DeploymentId = null;
        public static string DeploymentIdPrefix = null;
        public Guid ServiceId { get { return SiloOptions.ServiceId; } }

        public const int BasePort = 11111;
        public const int ProxyBasePort = 30000;
        private static int InstanceCounter = 0;
        internal static readonly SafeRandom random = TestConstants.random;

        /// <summary>
        ///Gets or sets the test context which provides
        ///information about and functionality for the current test run.
        ///</summary>
        public TestContext TestContext
        {
            get
            {
                return testContextInstance;
            }
            set
            {
                testContextInstance = value;

                // This code will be run at the start of each test case
                // After class & instance constructors 
                // and after ClassInit but before TestInit test framework callbacks

                if (testContextInstance == null) return;

                // Default silos will be running by now, because they are started during instance construction
                foreach (var siloHandle in new [] { Primary, Secondary })
                {
                    if (siloHandle == null) continue;
                    if (siloHandle.Silo == null) continue;
                    if (siloHandle.Silo.LocalConfig == null) continue;
                    if (siloHandle.Silo.LocalConfig.TraceFileName == null) continue;

                    // Add silo log file for each active silo to be collected in to test results
                    testContextInstance.AddResultFile(siloHandle.Silo.LocalConfig.TraceFileName);
                }
                // If there are also any additional silos running now, then link in their logs too
                foreach (var siloHandle in additionalSilos)
                {
                    if (siloHandle == null) continue;
                    if (siloHandle.Silo == null) continue;
                    if (siloHandle.Silo.LocalConfig == null) continue;
                    if (siloHandle.Silo.LocalConfig.TraceFileName == null) continue;

                    // Add silo log file for each active silo to be collected in to test results
                    testContextInstance.AddResultFile(siloHandle.Silo.LocalConfig.TraceFileName);
                }
                // Add client log file to be collected in to test results
                if (ClientConfig != null)
                {
                    testContextInstance.AddResultFile(ClientConfig.TraceFileName);
                }
            }
        }
        private TestContext testContextInstance;
        private static List<string> resultFiles = new List<string>(); 


        public UnitTestBase()
            : this(new Options())
        {
        }

        public UnitTestBase(bool startFreshOrleans)
            : this(new Options { StartFreshOrleans = startFreshOrleans })
        {
        }

        public UnitTestBase(Options siloOptions)
            : this(siloOptions, null)
        {
        }

        public UnitTestBase(Options siloOptions, ClientOptions clientOptions)
        {
            // Only show time in test logs, not date+time.
            TraceLogger.ShowDate = false;

            this.SiloOptions = siloOptions;
            this.ClientOptions = clientOptions;

            logger = TraceLogger.GetLogger("UnitTestBase-" + this.GetType().Name, TraceLogger.LoggerType.Application);

            AppDomain.CurrentDomain.UnhandledException += ReportUnobservedException;
            InitializeRuntime(this.GetType().FullName);
        }

        private void InitializeRuntime(string testName)
        {
            try
            {
                Initialize(SiloOptions, ClientOptions);
                string bars = "--------------------";
                string startMsg = string.Format("{0} STARTING NEW UNIT TEST : {1} {0}", bars, testName);
                logger.Info(0, startMsg);
                Console.WriteLine(startMsg);
            }
            catch (TimeoutException te)
            {
                Exception ex = new TimeoutException("Timeout during test initialization", te);

                HandleUnitTestSiloFailedToStart(ex);
                //// Not Reached
                throw;
            }
            catch (Exception ex)
            {
                HandleUnitTestSiloFailedToStart(ex);
                //// Not Reached
                throw;
            }
        }

        private static void HandleUnitTestSiloFailedToStart(Exception exc)
        {
            Console.WriteLine("HandleUnitTestSiloFailedToStart {0}", exc);
            Exception baseExc = exc.GetBaseException();

            string error;
            if (baseExc is TimeoutException)
            {
                error = "Timeout during test initialization";
            }
            else
            {
                error = "Exception during test initialization: " + TraceLogger.PrintException(exc);
            }

            try
            {
                var log = TraceLogger.GetLogger("UnitTestBase", TraceLogger.LoggerType.Application);
                log.Info(-1, error);
            }
            catch
            {
                // Ignore any problems writing to console.
            }

            try
            {
                Console.Error.WriteLine(error);
            }
            catch
            {
                // Ignore any problems writing to console.
            }

            // Null out current silos so that next test class is forced to start with fresh copy
            Primary = null;
            Secondary = null;

            Assert.Inconclusive("Error initializing UnitTest Silo : {0} {1}", error, exc);

            //// This code is not reached
        }

        private static void ReportUnobservedException(object sender, UnhandledExceptionEventArgs eventArgs)
        {
            Exception exception = (Exception) eventArgs.ExceptionObject;
            Console.WriteLine("Unobserved exception: {0}", exception);
            Assert.Fail("Unobserved exception: {0}", exception);
        }

        protected void WaitForLivenessToStabilize(bool softKill = true)
        {
            TimeSpan stabilizationTime = TimeSpan.Zero;
            if (!softKill)
            {
                // in case  of hard kill (kill and not Stop), we should give silos time to detect failures first.
                stabilizationTime = Globals.ProbeTimeout.Multiply(Globals.NumMissedProbesLimit);
            }
            if (Globals.UseLivenessGossip)
            {
                stabilizationTime += TimeSpan.FromSeconds(5);
            }
            else
            {
                stabilizationTime += Globals.TableRefreshTimeout.Multiply(2);
            }
            logger.Info("\n\nWaitForLivenessToStabilize is about to sleep for {0}", stabilizationTime);
            Thread.Sleep(stabilizationTime);
            logger.Info("WaitForLivenessToStabilize is done sleeping");
        }


        public static void Initialize(Options options, ClientOptions clientOptions = null)
        {
            bool doStartPrimary = false;
            bool doStartSecondary = false;

            if (!cleanedFileStore)
            {
                cleanedFileStore = true;
                EmptyFileStore();
                EmptyMembershipTable(); // first time
            }
            if (options.StartFreshOrleans)
            {
                if (additionalSilos.Count > 0)
                {
                    ResetAllAdditionalRuntimes();
                }

                // the previous test was !startFresh, so we need to cleanup after it.
                if (Primary != null || Secondary != null || RuntimeClient.Current != null)
                {
                    ResetDefaultRuntimes();
                }

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
                if (options.StartPrimary && Primary == null)
                {
                    // first time.
                    doStartPrimary = true;
                }
                if (options.StartSecondary && Secondary == null)
                {
                    doStartSecondary = true;
                }

                // Check that we don't have any old silo instances hanging around from previous test(s)
                if ((doStartPrimary || doStartSecondary)
                    && additionalSilos.Count != 0)
                {
                    StringBuilder sb = new StringBuilder("Need to ");
                    if (doStartPrimary) sb.Append("start new Primary ");
                    if (doStartSecondary) sb.Append("start new Secondary ");
                    sb.AppendFormat(" but {0} old additional silos exist", additionalSilos.Count);

                    throw new InvalidOperationException(sb.ToString());
                }
            }
            // Every time we start fresh silso, or first time if we don't alraecy have silos started, pick new deployment and service id.
            if (options.StartFreshOrleans || doStartPrimary || doStartSecondary)
            {
                string prefix = DeploymentIdPrefix != null ? DeploymentIdPrefix : "depid-";
                //DeploymentId = prefix + Guid.NewGuid().ToString();
                DateTime now = DateTime.UtcNow;
                string DateTimeFormat = "yyyy-MM-dd-hh-mm-ss-fff";
                int randomSuffix = random.Next(1000);
                DeploymentId = prefix + now.ToString(DateTimeFormat, CultureInfo.InvariantCulture) + "-" + randomSuffix;
            }

            if (doStartPrimary)
            {
                Primary = StartOrleansRuntime(Silo.SiloType.Primary, options);
            }
            if (doStartSecondary)
            {
                Secondary = StartOrleansRuntime(Silo.SiloType.Secondary, options);
            }

            if (RuntimeClient.Current == null && options.StartClient)
            {
                ClientConfiguration clientConfig;
                if (clientOptions != null && clientOptions.ClientConfigFile != null)
                {
                    clientConfig = ClientConfiguration.LoadFromFile(clientOptions.ClientConfigFile.FullName);
                }
                else
                {
                    clientConfig = ClientConfiguration.StandardLoad();
                }

                if (clientOptions != null)
                {
                    clientConfig.ResponseTimeout = clientOptions.ResponseTimeout;
                    if (clientOptions.GatewayProvider != ClientConfiguration.GatewayProviderType.None)
                    {
                        clientConfig.GatewayProvider = clientOptions.GatewayProvider;
                    }
                    if (clientOptions.ProxiedGateway && clientOptions.Gateways != null)
                    {
                        clientConfig.Gateways = clientOptions.Gateways;
                        if (clientOptions.PreferedGatewayIndex >= 0)
                            clientConfig.PreferedGatewayIndex = clientOptions.PreferedGatewayIndex;
                    }
                    clientConfig.PropagateActivityId = clientOptions.PropagateActivityId;
                    if (!String.IsNullOrEmpty(clientOptions.DataConnectionString))
                    {
                        clientConfig.DataConnectionString = clientOptions.DataConnectionString;
                    }
                }
                if (!String.IsNullOrEmpty(DeploymentId))
                {
                    clientConfig.DeploymentId = DeploymentId;
                    //clientConfig.ServiceId = ServiceId;
                }
                if (System.Diagnostics.Debugger.IsAttached)
                {
                    // Test is running inside debugger - Make timeout ~= infinite
                    clientConfig.ResponseTimeout = TimeSpan.FromMilliseconds(1000000);
                }
                if (options.LargeMessageWarningThreshold > 0)
                {
                    clientConfig.LargeMessageWarningThreshold = options.LargeMessageWarningThreshold;
                }
                clientConfig.AdjustConfiguration();

                ClientConfig = clientConfig;
                Trace.TraceInformation("Client config = {0}", ClientConfig);

                UnobservedExceptionsHandlerClass.ResetUnobservedExceptionHandler();
                if (!GrainClient.IsInitialized)
                {
                    GrainClient.Initialize(ClientConfig);
                }
            }
        }

        //public static bool ClientUnobservedPromiseHandler(Exception ex)
        //{
        //    var logger = new Logger("UnitTestBase", Logger.LoggerType.Application);
        //    logger.Error("Unobserved promise was broken with exception: ", ex);
        //    Assert.Fail("Unobserved promise was broken with exception: " + ex.Message + " at " + ex.StackTrace);
        //    return true;
        //}

        protected static void StartSecondGrainClient()
        {
            var grainClient = new OutsideRuntimeClient(ClientConfig, true);
            RuntimeClient.Current = grainClient;
            grainClient.StartInternal();
        }

        static SiloHandle StartOrleansRuntime(Silo.SiloType type, Options options, AppDomain shared = null)
        {
            SiloHandle retValue = new SiloHandle();
            StartOrleansRuntime(retValue, type, options, shared);
            return retValue;
        }
        static SiloHandle StartOrleansRuntime(SiloHandle retValue, Silo.SiloType type, Options options, AppDomain shared = null)
        {
            retValue.Options = options;
            retValue.IsInProcess = !options.StartOutOfProcess;
            ClusterConfiguration config = new ClusterConfiguration();
            if (options.SiloConfigFile == null)
            {
                config.StandardLoad();
            }
            else
            {
                config.LoadFromFile(options.SiloConfigFile.FullName);
            }
            // IMPORTANT: Do NOT uncomment this line! It hard-overwrittes the config setting, so we can't use any other trace level aside INFO.
            //config.Defaults.DefaultTraceLevel = Logger.Severity.Info;
            if (config.Globals.SeedNodes.Count > 0 && options.BasePort < 0)
            {
                config.PrimaryNode = config.Globals.SeedNodes[0];
            }
            else
            {
                config.PrimaryNode = new IPEndPoint(IPAddress.Loopback,
                                                    options.BasePort >= 0 ? options.BasePort : BasePort);
            }
            config.Globals.SeedNodes.Clear();
            config.Globals.SeedNodes.Add(config.PrimaryNode);

            config.Globals.ServiceId = options.ServiceId;
            if (!String.IsNullOrEmpty(DeploymentId))
            {
                config.Globals.DeploymentId = DeploymentId;
            }
            config.Defaults.PropagateActivityId = options.PropagateActivityId;
            if (options.LargeMessageWarningThreshold > 0) config.Defaults.LargeMessageWarningThreshold = options.LargeMessageWarningThreshold;

            if (!String.IsNullOrEmpty(options.DataConnectionString))
            {
                config.Globals.DataConnectionString = options.DataConnectionString;
            }

            if (options.LivenessType != GlobalConfiguration.LivenessProviderType.NotSpecified)
            {
                config.Globals.LivenessType = options.LivenessType;
            }
            else if (config.Globals.LivenessType == GlobalConfiguration.LivenessProviderType.NotSpecified)
            {
                config.Globals.LivenessType = GlobalConfiguration.LivenessProviderType.MembershipTableGrain;
            }
            // else use LivenessType value from config file

            if (options.ReminderServiceType != GlobalConfiguration.ReminderServiceProviderType.NotSpecified)
            {
                config.Globals.SetReminderServiceType(options.ReminderServiceType);
            }
            else if (config.Globals.ReminderServiceType == GlobalConfiguration.ReminderServiceProviderType.NotSpecified)
            {
                config.Globals.SetReminderServiceType(GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain);
            }
            // else use ReminderServiceType value from config file

            if (System.Diagnostics.Debugger.IsAttached)
            {
                config.Globals.TableRefreshTimeout = TimeSpan.FromMinutes(5);
                config.Globals.ProbeTimeout = TimeSpan.FromMinutes(5);
                config.Globals.IAmAliveTablePublishTimeout = TimeSpan.FromMinutes(5);
                config.Globals.DeploymentLoadPublisherRefreshTime = TimeSpan.FromMinutes(5);
            }
            Globals = config.Globals;
            config.IsRunningAsUnitTest = true;

            string domainName;
            switch (type)
            {
                case Silo.SiloType.Primary:
                    domainName = "Primary";
                    break;
                default:
                    domainName = "Secondary_" + InstanceCounter.ToString(CultureInfo.InvariantCulture);
                    break;
            }

            NodeConfiguration nodeConfig = config.GetConfigurationForNode(domainName);
            nodeConfig.HostNameOrIPAddress = "loopback";
            int port = options.BasePort < 0 ? BasePort : options.BasePort;
            nodeConfig.Port = port + InstanceCounter;
            if (nodeConfig.ProxyGatewayEndpoint != null && nodeConfig.ProxyGatewayEndpoint.Address != null)
            {
                nodeConfig.ProxyGatewayEndpoint = new IPEndPoint(nodeConfig.ProxyGatewayEndpoint.Address, ProxyBasePort + InstanceCounter);
            }
            nodeConfig.DefaultTraceLevel = config.Defaults.DefaultTraceLevel;
            nodeConfig.PropagateActivityId = config.Defaults.PropagateActivityId;
            nodeConfig.BulkMessageLimit = config.Defaults.BulkMessageLimit;
            nodeConfig.MaxActiveThreads = options.MaxActiveThreads;
            if (options.SiloGenerationNumber > 0)
            {
                nodeConfig.Generation = options.SiloGenerationNumber;
            }
            config.Globals.MaxForwardCount = options.MaxForwardCount;
            if (options.performDeadlockDetection != BooleanEnum.None) // use only if was explicitly specified.
                config.Globals.PerformDeadlockDetection = options.PerformDeadlockDetection;
            SerializationManager.Initialize(config.Globals.UseStandardSerializer);

            if (config.Globals.ExpectedClusterSizeConfigValue.IsDefaultValue) // overwrite only if was not explicitly set.
                config.Globals.ExpectedClusterSize = 2;

            config.Globals.CollectionQuantum = options.CollectionQuantum;
            config.Globals.Application.SetDefaultCollectionAgeLimit(options.DefaultCollectionAgeLimit);

            InstanceCounter++;

            config.Overrides[domainName] = nodeConfig;
            config.AdjustConfiguration();

            if (options.StartOutOfProcess)
            {
                //save the config so that you can pass it can be passed to the OrleansHost process.
                //launch the process
                retValue.Endpoint = nodeConfig.Endpoint;
                string fileName = Path.Combine(Path.GetDirectoryName(typeof(UnitTestBase).Assembly.Location), 
                    "UnitTestSilo-" + domainName + "-" + DateTime.UtcNow.Ticks + ".txt");
                WriteConfigFile(fileName, config);
                //testContextInstance.AddResultFile(fileName);
                retValue.MachineName = options.MachineName;
                string processName = "OrleansHost.exe";
                string imagePath = Path.Combine(Path.GetDirectoryName(typeof(UnitTestBase).Assembly.Location), processName);
                retValue.Process = StartProcess(retValue.MachineName, imagePath, domainName, fileName);
            }
            else
            {
                Trace.TraceInformation("Starting a new {0} silo in app domain {1} with config = {2}", 
                    type, domainName, config.ToString(domainName));

                AppDomainSetup setup = GetAppDomainSetupInfo();
                AppDomain outDomain = AppDomain.CreateDomain(domainName, null, setup);
                var args = new object[] { domainName, type, config };
                Silo silo = (Silo) outDomain.CreateInstanceFromAndUnwrap(
                    "OrleansRuntime.dll", typeof(Silo).FullName, false,
                    BindingFlags.Default, null, args, CultureInfo.CurrentCulture,
                    new object[] { });

                resultFiles.Add(silo.LocalConfig.TraceFileName);

                silo.Start();
                retValue.Silo = silo;
                retValue.Endpoint = silo.SiloAddress.Endpoint;
                retValue.AppDomain = outDomain;
                retValue.AppDomain.UnhandledException += ReportUnobservedException;

            }
            retValue.Name = domainName;
            return retValue;
        }

        protected SiloHandle StartAdditionalOrleans()
        {
            SiloHandle instance = StartOrleansRuntime(
                Silo.SiloType.Secondary,
                this.SiloOptions);
            additionalSilos.Add(instance);
            
            //if (testContextInstance != null)
            //{
            //    // Add silo log file for each active silo
            //    testContextInstance.AddResultFile(instance.Silo.LocalConfig.TraceFileName);
            //}
            return instance;
        }

        protected IEnumerable<SiloHandle> GetActiveSilos()
        {
            logger.Info("GetActiveSilos: Primary={0} Secondary={1} + {2} Additional={3}",
                Primary, Secondary, additionalSilos.Count, Utils.EnumerableToString(additionalSilos));

            if (null != Primary && Primary.Silo != null) yield return Primary;
            if (null != Secondary && Secondary.Silo != null) yield return Secondary;
            if (additionalSilos.Count > 0)
                foreach (var s in additionalSilos)
                    if (null != s && s.Silo != null)
                        yield return s;
        }

        protected SiloHandle GetSiloForAddress(SiloAddress siloAddress)
        {
            var ret = GetActiveSilos().Where(s => s.Silo.SiloAddress.Equals(siloAddress)).FirstOrDefault();
            return ret;
        }

        protected List<SiloHandle> StartAdditionalOrleansRuntimes(int nRuntimes)
        {
            List<SiloHandle> instances = new List<SiloHandle>();
            for (int i = 0; i < nRuntimes; i++)
            {
                SiloHandle instance = StartAdditionalOrleans();
                instances.Add(instance);
            }
            return instances;
        }

        public static void ResetAllAdditionalRuntimes()
        {
            if (additionalSilos.Count == 0) return;

            Console.WriteLine("Stopping all additional silos - {0}", DumpSiloList());

            if (Primary == null && Secondary == null)
            {
                string error = "***** WARNING: Always stop additional silos before stopping the default silos.";
                Console.WriteLine(error);
                //throw new InvalidOperationException(error);
            }

            foreach (SiloHandle instance in additionalSilos.ToArray())
            {
                try
                {
                    ResetRuntime(instance);
                }
                catch (Exception)
                {
                    //Console.WriteLine("Ignoring exception while shutting down silo {0} {1}",
                    //    instance.Silo, exc);
                }
            }
            additionalSilos.Clear();
        }

        public static void ResetDefaultRuntimes()
        {
            Console.WriteLine("Stopping default silos - {0}", DumpSiloList());

            foreach (SiloHandle instance in new[] { Secondary, Primary })
            {
                if (instance == null) continue;
                try
                {
                    ResetRuntime(instance);
                }
                catch (Exception exc)
                {
                    Console.WriteLine("Ignoring exception while shutting down silo {0} {1}", instance.Silo, exc);
                }
            }
            Secondary = null;
            Primary = null;

            EmptyMembershipTable();

            Console.WriteLine("Stopping client");
            try
            {
                GrainClient.Uninitialize();
            }
            catch (Exception exc)
            {
                Console.WriteLine("Ignoring exception doing GrainClient.Uninitialize {0}", exc);
            }

            Console.WriteLine("Resetting DeploymentId / ServiceId");
            InstanceCounter = 0;
            DeploymentId = null;
        }

        private static string DumpSiloList()
        {
            try
            {
                return string.Format("Primary={0} Secondary={1} Additional={2}",
                    Primary == null ? "null silo handle"
                        : Primary.Silo == null ? "null silo"
                            : Primary.Silo.SiloAddress.ToLongString(),
                    Secondary == null ? "null silo handle"
                        : Secondary.Silo == null ? "null silo"
                            : Secondary.Silo.SiloAddress.ToLongString(),
                    Utils.EnumerableToString(additionalSilos, s =>
                    {
                        if (s == null) return "null entry";
                        if (s.Silo == null) return "null silo";
                        if (s.Silo.SiloAddress == null) return s.Silo.ToString();
                        return s.Silo.SiloAddress.ToLongString();
                    }));
            }
            catch (Exception exc)
            {
                string error = "Unable to get Silo list: " + exc;
                Console.WriteLine(error);
                return error;
            }
        }

        private static AppDomainSetup GetAppDomainSetupInfo()
        {
            AppDomain currentAppDomain = AppDomain.CurrentDomain;

            return new AppDomainSetup
            {
                ApplicationBase = Environment.CurrentDirectory,
                ConfigurationFile = currentAppDomain.SetupInformation.ConfigurationFile,
                ShadowCopyFiles = currentAppDomain.SetupInformation.ShadowCopyFiles,
                ShadowCopyDirectories = currentAppDomain.SetupInformation.ShadowCopyDirectories,
                CachePath = currentAppDomain.SetupInformation.CachePath
            };
        }

        private static void DoStopSilo(SiloHandle instance, bool kill)
        {
            Console.WriteLine("About to {0} silo {1}", kill ? "Kill" : "Stop", instance.Endpoint);

            if (instance.IsInProcess)
            {
                if (!kill)
                {
                    try { if (instance.Silo != null) instance.Silo.Stop(); }
                    catch (RemotingException re) { Console.WriteLine(re); /* Ignore error */ }
                    catch (Exception exc) { Console.WriteLine(exc); throw; }
                }

                try
                {
                    if (instance.AppDomain != null)
                    {
                        instance.AppDomain.UnhandledException -= ReportUnobservedException;
                        AppDomain.Unload(instance.AppDomain);
                    }
                }
                catch (Exception exc) { Console.WriteLine(exc); throw; }
            }
            else
            {
                try { if (instance.Process != null) instance.Process.Kill(); }
                catch (Exception exc) { Console.WriteLine(exc); throw; }
            }
            instance.AppDomain = null;
            instance.Silo = null;
            instance.Process = null;
        }

        public static void StopRuntime(SiloHandle instance)
        {
            if (instance != null)
            {
                if (!instance.IsInProcess)
                {
                    throw new NotSupportedException(
                        "Cannot stop silo when running out of processes, can only kill silo.");
                }
                else
                {
                    DoStopSilo(instance, false);
                }
            }
        }

        public static void KillRuntime(SiloHandle instance)
        {
            if (instance != null)
            {
                // do NOT stop, just kill directly, to simulate crash.
                DoStopSilo(instance, true);
            }
        }

        public static void ResetRuntime(SiloHandle instance)
        {
            if (instance != null)
            {
                DoStopSilo(instance, false);
            }
        }

        public static SiloHandle RestartRuntime(SiloHandle instance, bool kill = false)
        {
            if (instance != null)
            {
                var options = instance.Options;
                var type = instance.Silo.Type;
                DoStopSilo(instance, kill);
                StartOrleansRuntime(instance, type, options);
                return instance;
            }
            return null;
        }

        protected void RestartDefaultSilosButKeepCurrentClient(string msg)
        {
            logger.Info("Restarting all silos - Old Primary={0} Secondary={1} Others={2}",
                Primary.Silo.SiloAddress,
                Secondary.Silo.SiloAddress,
                Utils.EnumerableToString(additionalSilos, s => s.Silo.SiloAddress.ToString()));

            ResetAllAdditionalRuntimes();
            ResetRuntime(Secondary);
            ResetRuntime(Primary);
            Secondary = null;
            Primary = null;
            InstanceCounter = 0;

            Primary = StartOrleansRuntime(Silo.SiloType.Primary, SiloOptions);
            Secondary = StartOrleansRuntime(Silo.SiloType.Secondary, SiloOptions);

            logger.Info("After restarting silos - New Primary={0} Secondary={1} Others={2}",
                Primary.Silo.SiloAddress,
                Secondary.Silo.SiloAddress,
                Utils.EnumerableToString(additionalSilos, s => s.Silo.SiloAddress.ToString()));
        }

        protected SiloAddress[] GetRuntimesIds(List<Silo> instances)
        {
            SiloAddress[] ids = new SiloAddress[instances.Count];
            for (int i = 0; i < instances.Count; i++)
            {
                ids[i] = instances[i].SiloAddress;
            }
            return ids;
        }

        protected static void EmptyFileStore()
        {
            //ServerConfigManager configManager = ServerConfigManager.LoadConfigManager();
            // todo: clear StoreManager
        }

        protected static void EmptyMembershipTable()
        {
            try
            {
                ClusterConfiguration config = new ClusterConfiguration();
                config.StandardLoad();
                //if (config.Globals.LivenessType.Equals(GlobalConfiguration.LivenessProviderType.File))
                //{
                //    FileBasedMembershipTable.DeleteMembershipTableFile(config.Globals.LivenessFileDirectory);
                //}
                //else 
                if (config.Globals.LivenessType.Equals(GlobalConfiguration.LivenessProviderType.AzureTable))
                {
                    AzureBasedMembershipTable table = AzureBasedMembershipTable.GetMembershipTable(config.Globals, false)
                        .WithTimeout(AzureTableDefaultPolicies.TableOperationTimeout).Result;
                    table.DeleteMembershipTableEntries(config.Globals.DeploymentId)
                        .Wait(AzureTableDefaultPolicies.TableOperationTimeout);
                }
                if (config.Globals.LivenessType.Equals(GlobalConfiguration.LivenessProviderType.SqlServer))
                {
                    SqlMembershipTable table = SqlMembershipTable.GetMembershipTable(config.Globals, false)
                        .WithTimeout(AzureTableDefaultPolicies.TableOperationTimeout).Result;

                    table.DeleteMembershipTableEntries(config.Globals.DeploymentId)
                        .Wait(AzureTableDefaultPolicies.TableOperationTimeout);
                }
            }
            catch (Exception) { }
        }

        internal void DeleteAllAzureQueues(string providerName, string deploymentId, string storageConnectionString)
        {
            AzureQueueStreamProvider.DeleteAllUsedAzureQueues(providerName, deploymentId, storageConnectionString, this.logger).Wait();
        }

        internal static void DeleteAllAzureQueues(string providerName, string deploymentId, string storageConnectionString, Logger logger)
        {
            AzureQueueStreamProvider.DeleteAllUsedAzureQueues(providerName, deploymentId, storageConnectionString, logger).Wait();
        }

        //public static void RemoveFromRemoteMachine(string machineName, string path)
        //{
        //    Directory.Delete(@"\\" + machineName + @"\" + path.Replace(":", "$"), true);
        //}

        //public static void CopyToRemoteMachine(string machineName, string from, string to, string[] exclude = null)
        //{
        //    ProcessStartInfo startInfo = new ProcessStartInfo();
        //    startInfo.FileName = @"C:\Windows\System32\xcopy.exe";
        //    startInfo.CreateNoWindow = true;
        //    startInfo.UseShellExecute = false;
        //    startInfo.WorkingDirectory = Path.GetDirectoryName(typeof(UnitTestBase).Assembly.Location);
        //    StringBuilder args = new StringBuilder();
        //    args.AppendFormat(" {0} {1}", from, @"\\" + machineName + @"\" + to.Replace(":", "$"));
        //    if (null != exclude && exclude.Length > 0)
        //    {
        //        args.AppendFormat(" /EXCLUDE:{0}", string.Join("+", exclude));
        //    }
        //    args.AppendFormat(" /I /V /E /Y /Z");
        //    startInfo.Arguments = args.ToString();
        //    Process xcopy = Process.Start(startInfo);
        //    xcopy.WaitForExit();
        //}

        public static Process StartProcess(string machineName, string processPath, params string[] parameters)
        {
            StringBuilder args = new StringBuilder();
            foreach (string s in parameters)
            {
                args.AppendFormat(" \"{0}\" ", s);
            }
            if (machineName == "." || machineName == "localhost")
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = processPath;
                startInfo.CreateNoWindow = true;
                startInfo.WorkingDirectory = Path.GetDirectoryName(processPath);
                startInfo.UseShellExecute = false;
                startInfo.Arguments = args.ToString();
                Process retValue = Process.Start(startInfo);
                string startupEventName = parameters[0];
                bool createdNew;
                EventWaitHandle startupEvent = new EventWaitHandle(false, EventResetMode.ManualReset, startupEventName, out createdNew);
                if (!createdNew) startupEvent.Reset();
                bool b = startupEvent.WaitOne(15000);
                Assert.IsTrue(b);
                return retValue;
            }
            else
            {
                string commandline = string.Format("{0} {1}", processPath, args);
                // connect
                ConnectionOptions connOpt = new ConnectionOptions();
                connOpt.Impersonation = ImpersonationLevel.Impersonate;
                connOpt.EnablePrivileges = true;
                ManagementScope scope = new ManagementScope(String.Format(@"\\{0}\ROOT\CIMV2", machineName), connOpt);
                scope.Connect();

                ObjectGetOptions objectGetOptions = new ObjectGetOptions();
                ManagementPath managementPath = new ManagementPath("Win32_Process");
                ManagementClass processClass = new ManagementClass(scope, managementPath, objectGetOptions);
                ManagementBaseObject inParams = processClass.GetMethodParameters("Create");
                inParams["CommandLine"] = commandline;
                inParams["CurrentDirectory"] = Path.GetDirectoryName(processPath);

                ManagementBaseObject outParams = processClass.InvokeMethod("Create", inParams, null);
                int processId = int.Parse(outParams["processId"].ToString());
                return Process.GetProcessById(processId, machineName);
            }
        }
        static public void WriteConfigFile(string fileName, ClusterConfiguration config)
        {
            StringBuilder content = new StringBuilder();
            content.AppendFormat(@"<?xml version=""1.0"" encoding=""utf-8""?>
<OrleansConfiguration xmlns=""urn:orleans"">
  <Globals>
    {0}
    <SeedNode Address=""localhost"" Port=""11111"" />
    <Tasks Disabled=""true""/>
    <Messaging ResponseTimeout=""3000"" SiloSenderQueues=""5"" MaxResendCount=""0""/>
    {1}
  </Globals>
  <Defaults>
    <Networking Address=""localhost"" Port=""0"" />
    <Scheduler MaxActiveThreads=""0"" />
    <Tracing DefaultTraceLevel=""Info"" TraceToConsole=""true"" TraceToFile=""{{0}}-{{1}}.log"" WriteMessagingTraces=""false""/>
  </Defaults>
  <Override Node=""Primary"">
	  <Networking Port=""11111"" />
	  <ProxyingGateway Address=""localhost"" Port=""30000"" />
  </Override>
	<Override Node=""Secondary_1"">
    <Networking Port=""11112"" />
    <ProxyingGateway Address=""localhost"" Port=""30001"" />
  </Override>
  <Override Node=""Node2"">
    <Networking Port=""11113"" />
  </Override>
  <Override Node=""Node3"">
    <Networking Port=""11114"" />
  </Override>
</OrleansConfiguration>",
                        ProviderCategoryConfiguration.ProviderConfigsToXmlString(config.Globals.ProviderConfigurations),
                        ToXmlString(config.Globals.Application)
                        );

            using (StreamWriter writer = new StreamWriter(fileName))
            {
                writer.WriteLine(content.ToString());
                writer.Flush();
            }
        }
        private static string ToXmlString(ApplicationConfiguration appConfig)
        {
            StringBuilder result = new StringBuilder();
            result.AppendFormat("            <Application>");
            result.AppendFormat("                <Defaults>");
            result.AppendFormat("                    <Deactivation AgeLimit=\"{0}\"/>", (long) appConfig.DefaultCollectionAgeLimit.TotalSeconds);
            result.AppendFormat("                </Defaults>");
            foreach (GrainTypeConfiguration classConfig in appConfig.ClassSpecific)
            {
                if (classConfig.CollectionAgeLimit.HasValue)
                {
                    result.AppendFormat("                <GrainType Type=\"{0}\">", classConfig.Type.FullName);
                    result.AppendFormat("                    <Deactivation AgeLimit=\"{0}\"/>", (long) classConfig.CollectionAgeLimit.Value.TotalSeconds);
                    result.AppendFormat("                </GrainType>");
                }
            }
            result.AppendFormat("            </Application>");
            return result.ToString();
        }

        public static void RunScript(string scriptPath, params string[] options)
        {
            Command command = new Command(scriptPath + " " + string.Join(" ", options), true);

            RunspaceConfiguration runspaceConfiguration = RunspaceConfiguration.Create();

            Runspace runspace = RunspaceFactory.CreateRunspace(runspaceConfiguration);
            runspace.Open();

            RunspaceInvoke scriptInvoker = new RunspaceInvoke(runspace);

            using (Pipeline pipeline = runspace.CreatePipeline())
            {
                pipeline.Commands.Add(command);

                try
                {
                    var results = pipeline.Invoke();
                }
                catch (Exception e)
                {
                    Console.WriteLine("Error: " + e);
                }
            }
        }

        public static TimeSpan TimeRun(int numIterations, TimeSpan baseline, string what, Action action)
        {
            var stopwatch = new Stopwatch();

            long startMem = GC.GetTotalMemory(true);
            stopwatch.Start();

            action();

            stopwatch.Stop();
            long stopMem = GC.GetTotalMemory(false);
            long memUsed = stopMem - startMem;
            TimeSpan duration = stopwatch.Elapsed;

            string timeDeltaStr = "";
            if (baseline > TimeSpan.Zero)
            {
                double delta = (duration - baseline).TotalMilliseconds / baseline.TotalMilliseconds;
                timeDeltaStr = String.Format("-- Change = {0}%", 100.0 * delta);
            }
            Console.WriteLine("Time for {0} loops doing {1} = {2} {3} Memory used={4}", numIterations, what, duration, timeDeltaStr, memUsed);
            return duration;
        }

        public static int GetRandomGrainId()
        {
            return random.Next();
        }


        public static void ConfigureClientThreadPoolSettingsForStorageTests(int NumDotNetPoolThreads = 200)
        {
            ThreadPool.SetMinThreads(NumDotNetPoolThreads, NumDotNetPoolThreads);
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.DefaultConnectionLimit = NumDotNetPoolThreads; // 1000;
            ServicePointManager.UseNagleAlgorithm = false;
        }

        public static async Task WaitUntilAsync(Func<Task<bool>> predicate, TimeSpan timeout)
        {
            bool keepGoing = true;
            int numLoops = 0;
            // ReSharper disable AccessToModifiedClosure
            Func<Task> loop =
                async () =>
                {
                    do
                    {
                        numLoops++;
                        // need to wait a bit to before re-checking the condition.
                        await Task.Delay(TimeSpan.FromSeconds(1));
                    }
                    while (!await predicate() && keepGoing);
                };
            // ReSharper restore AccessToModifiedClosure

            var task = loop();
            try
            {
                await Task.WhenAny(new Task[] { task, Task.Delay(timeout) });
            }
            finally
            {
                keepGoing = false;
            }
            Assert.IsTrue(task.IsCompleted, "The test completed {0} loops then timed out after {1}", numLoops, timeout);
        }

        public void SuppressFastKillInHandleProcessExit()
        {
            foreach (var silo in GetActiveSilos())
            {
                if (silo != null && silo.Silo != null && silo.Silo.TestHookup != null)
                {
                    silo.Silo.TestHookup.SuppressFastKillInHandleProcessExit();
                }
            }
        }

        public static void SuppressFastKillInHandleProcessExit_Static()
        {
            if (Primary != null && Primary.Silo != null && Primary.Silo.TestHookup != null) Primary.Silo.TestHookup.SuppressFastKillInHandleProcessExit();
            if (Secondary != null && Secondary.Silo != null && Secondary.Silo.TestHookup != null) Secondary.Silo.TestHookup.SuppressFastKillInHandleProcessExit();
            if (additionalSilos != null)
            {
                foreach (var silo in additionalSilos)
                {
                    if (silo != null && silo.Silo != null && silo.Silo.TestHookup != null)
                    {
                        silo.Silo.TestHookup.SuppressFastKillInHandleProcessExit();
                    }
                }
            }
        }


        public static double CalibrateTimings()
        {
            const int NumLoops = 10000;
            TimeSpan baseline = TimeSpan.FromTicks(80); // Baseline from jthelin03D
            int n;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < NumLoops; i++)
            {
                n = i;
            }
            sw.Stop();
            double multiple = 1.0 * sw.ElapsedTicks / baseline.Ticks;
            Console.WriteLine("CalibrateTimings: {0} [{1} Ticks] vs {2} [{3} Ticks] = x{4}",
                sw.Elapsed, sw.ElapsedTicks,
                baseline, baseline.Ticks,
                multiple);
            return multiple > 1.0 ? multiple : 1.0;
        }

        protected void TestSilosStarted(int expected)
        {
            IManagementGrain mgmtGrain = ManagementGrainFactory.GetGrain(RuntimeInterfaceConstants.SYSTEM_MANAGEMENT_ID);

            Dictionary<SiloAddress, SiloStatus> statuses = mgmtGrain.GetHosts(onlyActive: true).Result;
            foreach (var pair in statuses)
            {
                Console.WriteLine("       ######## Silo {0}, status: {1}", pair.Key, pair.Value);
                Assert.AreEqual(
                    SiloStatus.Active,
                    pair.Value,
                    "Failed to confirm start of {0} silos ({1} confirmed).",
                    pair.Value,
                    SiloStatus.Active);
            }
            Assert.AreEqual(expected, statuses.Count);
        }

        public static async Task<int> GetActivationCount(string fullTypeName)
        {
            int result = 0;

            IManagementGrain mgmtGrain = ManagementGrainFactory.GetGrain(RuntimeInterfaceConstants.SYSTEM_MANAGEMENT_ID);
            SimpleGrainStatistic[] stats = await mgmtGrain.GetSimpleGrainStatistics();
            foreach (var stat in stats)
            {
                if (stat.GrainType == fullTypeName)
                    result += stat.ActivationCount;
            }
            return result;
        }

        internal static OrleansTaskScheduler InitializeSchedulerForTesting(ISchedulingContext context)
        {
            StatisticsCollector.StatisticsCollectionLevel = StatisticsLevel.Info;
            SchedulerStatisticsGroup.Init();
            var scheduler = new OrleansTaskScheduler(4);
            LimitManager.Initialize(new DummyLimitsConfiguration());
            scheduler.Start();
            WorkItemGroup ignore = scheduler.RegisterWorkContext(context);
            return scheduler;
        }
    }

    internal class DummyLimitsConfiguration : ILimitsConfiguration
    {
        internal DummyLimitsConfiguration()
        {
            LimitValues = new Dictionary<string, LimitValue>();
        }

        public IDictionary<string, LimitValue> LimitValues { get; private set; }

        public LimitValue GetLimit(string name)
        {
            return null;
        }

    }

    public enum BooleanEnum
    {
        None = 0,
        True = 1,
        False = 2
    }

    public class Options
    {
        public Options()
        {
            // all defaults except:
            StartFreshOrleans = true;
            StartPrimary = true;
            StartSecondary = true;
            StartClient = true;
            ServiceId = Guid.NewGuid();
            DataConnectionString = null;
            ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.NotSpecified;
            MaxActiveThreads = 5;
            PropagateActivityId = Constants.DEFAULT_PROPAGATE_E2E_ACTIVITY_ID;
            BasePort = -1; // use default from configuration file
            MaxForwardCount = MessagingConfiguration.DEFAULT_MAX_FORWARD_COUNT;
            MachineName = ".";
            StartOutOfProcess = GetConfigFlag("StartOutOfProcess", false);
            SiloGenerationNumber = -1;
            performDeadlockDetection = BooleanEnum.None;
            LargeMessageWarningThreshold = 0;
            LivenessType = GlobalConfiguration.LivenessProviderType.NotSpecified;
            //StartOutOfProcess = GetConfigFlag("StartOutOfProcess",true);
            //UseMockTable = true;
            //UseMockOracle = true;
            CollectionQuantum = GlobalConfiguration.DEFAULT_COLLECTION_QUANTUM;
            DefaultCollectionAgeLimit = GlobalConfiguration.DEFAULT_COLLECTION_AGE_LIMIT;
        }

        private bool GetConfigFlag(string name, bool defaultValue)
        {
            bool result;
            if (bool.TryParse(ConfigurationManager.AppSettings[name], out result))
                return result;
            else return defaultValue;
        }
        public Options Copy()
        {
            return new Options
            {
                StartOutOfProcess = StartOutOfProcess,
                StartFreshOrleans = StartFreshOrleans,
                StartPrimary = StartPrimary,
                StartSecondary = StartSecondary,
                StartClient = StartClient,
                ServiceId = ServiceId,
                SiloConfigFile = SiloConfigFile,
                DataConnectionString = DataConnectionString,
                ReminderServiceType = ReminderServiceType,
                MaxActiveThreads = MaxActiveThreads,
                BasePort = BasePort,
                MaxForwardCount = MaxForwardCount,
                MachineName = MachineName,
                LargeMessageWarningThreshold = LargeMessageWarningThreshold,

                DefaultCollectionAgeLimit = DefaultCollectionAgeLimit,
                CollectionQuantum = CollectionQuantum,
                SiloGenerationNumber = SiloGenerationNumber,

                PerformDeadlockDetection = PerformDeadlockDetection,
                PropagateActivityId = PropagateActivityId,
                LivenessType = LivenessType
            };
        }
        public bool StartOutOfProcess { get; set; }
        public bool StartFreshOrleans { get; set; }
        public bool StartPrimary { get; set; }
        public bool StartSecondary { get; set; }
        public bool StartClient { get; set; }

        public Guid ServiceId { get; private set; }

        public FileInfo SiloConfigFile { get; set; }
        public string DataConnectionString { get; set; }
        public GlobalConfiguration.ReminderServiceProviderType ReminderServiceType { get; set; }

        public int MaxActiveThreads { get; set; }
        public bool PropagateActivityId { get; set; }
        public int BasePort { get; set; }
        public int MaxForwardCount { get; set; }
        public string MachineName { get; set; }
        public int LargeMessageWarningThreshold { get; set; }

        public TimeSpan DefaultCollectionAgeLimit { get; set; }
        public TimeSpan CollectionQuantum { get; set; }
        public int SiloGenerationNumber { get; set; }
        public GlobalConfiguration.LivenessProviderType LivenessType { get; set; }

        internal BooleanEnum performDeadlockDetection;
        public bool PerformDeadlockDetection
        {
            get { return performDeadlockDetection == BooleanEnum.True; }
            set { performDeadlockDetection = (value ? BooleanEnum.True : BooleanEnum.False); }
        }
    }

    public class ClientOptions
    {
        public ClientOptions()
        {
            // all defaults except:
            ResponseTimeout = Constants.DEFAULT_RESPONSE_TIMEOUT;
            ProxiedGateway = false;
            Gateways = null;
            PreferedGatewayIndex = -1;
            PropagateActivityId = Constants.DEFAULT_PROPAGATE_E2E_ACTIVITY_ID;
            ClientConfigFile = null;
            GatewayProvider = ClientConfiguration.GatewayProviderType.None;
            DataConnectionString = null;
        }

        public ClientOptions Copy()
        {
            return new ClientOptions
            {
                ResponseTimeout = ResponseTimeout,
                ProxiedGateway = ProxiedGateway,
                Gateways = Gateways,
                PreferedGatewayIndex = PreferedGatewayIndex,
                PropagateActivityId = PropagateActivityId,
                ClientConfigFile = ClientConfigFile,
                GatewayProvider = GatewayProvider,
                DataConnectionString = DataConnectionString,
            };
        }
        public TimeSpan ResponseTimeout { get; set; }
        public bool ProxiedGateway { get; set; }
        public List<IPEndPoint> Gateways { get; set; }
        public int PreferedGatewayIndex { get; set; }
        public bool PropagateActivityId { get; set; }
        public FileInfo ClientConfigFile { get; set; }
        public ClientConfiguration.GatewayProviderType GatewayProvider { get; set; }
        public string DataConnectionString { get; set; }
    }
}
