using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.Remoting;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;

namespace Orleans.Samples.Testing
{
    /// <summary>
    /// A host class for local testing with Orleans using in-process silos. 
    /// Runs a Primary & Secondary silo in seperate app domains, and client in main app domain.
    /// Additional silos can also be started in-process if required for particular test cases.
    /// </summary>
    public class UnitTestSiloHost
    {
        protected static AppDomain SharedMemoryDomain;

        protected static SiloHandle Primary = null;
        protected static SiloHandle Secondary = null;
        protected static readonly List<SiloHandle> additionalSilos = new List<SiloHandle>();

        protected readonly UnitTestSiloOptions siloInitOptions;
        protected readonly UnitTestClientOptions clientInitOptions;

        protected static GlobalConfiguration globalConfig = null;     
        protected static ClientConfiguration clientConfig = null;
        
        public static string DeploymentId = null;
        public static string DeploymentIdPrefix = null;

        public const int BasePort = 11111;
        
        private static int InstanceCounter = 0;
        private static readonly Random random = new Random();

        /// <summary>
        /// Start the default Primary and Secondary test silos, plus client in-process, 
        /// using the default silo config options.
        /// </summary>
        public UnitTestSiloHost()
            : this(new UnitTestSiloOptions(), null)
        {
        }

        /// <summary>
        /// Start the default Primary and Secondary test silos, plus client in-process, 
        /// ensuring that fresh silos are started if they were already running.
        /// </summary>
        public UnitTestSiloHost(bool startFreshOrleans)
            : this(new UnitTestSiloOptions { StartFreshOrleans = startFreshOrleans }, null)
        {
        }

        /// <summary>
        /// Start the default Primary and Secondary test silos, plus client in-process, 
        /// using the specified silo config options.
        /// </summary>
        public UnitTestSiloHost(UnitTestSiloOptions siloOptions)
            : this(siloOptions, null)
        {
        }

        /// <summary>
        /// Start the default Primary and Secondary test silos, plus client in-process, 
        /// using the specified silo and client config options.
        /// </summary>
        public UnitTestSiloHost(UnitTestSiloOptions siloOptions, UnitTestClientOptions clientOptions)
        {
            this.siloInitOptions = siloOptions;
            this.clientInitOptions = clientOptions;

            AppDomain.CurrentDomain.UnhandledException += _ReportUnobservedException;

            try
            {
                _Initialize(siloOptions, clientOptions);
                string startMsg = "----------------------------- STARTING NEW UNIT TEST BASE: " + this.GetType().FullName + " -------------------------------------";
                Console.WriteLine(startMsg);
            }
            catch (TimeoutException te)
            {
                throw new TimeoutException("Timeout during test initialization", te);
            }
            catch (Exception ex)
            {
                Exception baseExc = ex.GetBaseException();
                if (baseExc is TimeoutException)
                {
                    throw new TimeoutException("Timeout during test initialization", ex);
                }
                throw new AggregateException(
                    string.Format("Exception during test initialization: {0}", 
                        Logger.PrintException(ex)), ex);
            }
        }

        /// <summary>
        /// Get the list of current active silos.
        /// </summary>
        /// <returns>List of current silos.</returns>
        public IEnumerable<SiloHandle> GetActiveSilos()
        {
            Console.WriteLine("GetActiveSilos: Primary={0} Secondary={1} + {2} Additional={3}",
                Primary, Secondary, additionalSilos.Count, additionalSilos);

            if (null != Primary && Primary.Silo != null) yield return Primary;
            if (null != Secondary && Secondary.Silo != null) yield return Secondary;
            if (additionalSilos.Count > 0)
                foreach (var s in additionalSilos)
                    if (null != s && s.Silo != null)
                        yield return s;
        }

        /// <summary>
        /// Find the silo handle for the specified silo address.
        /// </summary>
        /// <param name="siloAddress">Silo address to be found.</param>
        /// <returns>SiloHandle of the appropriate silo, or <c>null</c> if not found.</returns>
        public SiloHandle GetSiloForAddress(SiloAddress siloAddress)
        {
            var ret = GetActiveSilos().Where(s => s.Silo.SiloAddress.Equals(siloAddress)).FirstOrDefault();
            return ret;
        }

        /// <summary>
        /// Wait for the silo liveness sub-system to detect and act on any recent cluster membership changes.
        /// </summary>
        /// <param name="didKill">Whether recent membership changes we done by graceful Stop.</param>
        public void WaitForLivenessToStabilize(bool didKill = false)
        {
            TimeSpan stabilizationTime = TimeSpan.Zero;
            if(didKill)
            {
                // in case of hard kill (kill and not Stop), we should give silos time to detect failures first.
                stabilizationTime = _Multiply(globalConfig.ProbeTimeout, globalConfig.NumMissedProbesLimit);
            }
            if (globalConfig.UseLivenessGossip)
            {
                stabilizationTime += TimeSpan.FromSeconds(5);
            }
            else
            {
                stabilizationTime += _Multiply(globalConfig.TableRefreshTimeout, 2);
            }
            Console.WriteLine("\n\nWaitForLivenessToStabilize is about to sleep for {0}", stabilizationTime);
            Thread.Sleep(stabilizationTime);
            Console.WriteLine("WaitForLivenessToStabilize is done sleeping");
        }

        /// <summary>
        /// Start an additional silo, so that it joins the existing cluster with the default Primary and Secondary silos.
        /// </summary>
        /// <returns>SiloHandle for the newly started silo.</returns>
        public SiloHandle StartAdditionalSilo()
        {
            SiloHandle instance = _StartOrleansSilo(
                Silo.SiloType.Secondary,
                this.siloInitOptions);
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
        /// Stop the default Primary and Secondary silos.
        /// </summary>
        public void StopDefaultSilos()
        {
            try
            {
                Orleans.ActorClient.Uninitialize();
            }
            catch (Exception exc) { Console.WriteLine(exc); }

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
        }

        /// <summary>
        /// Restart the default Primary and Secondary silos.
        /// </summary>
        public void RestartDefaultSilos()
        {
            UnitTestSiloOptions primarySiloOptions = Primary.Options;
            UnitTestSiloOptions secondarySiloOptions = Secondary.Options;
            // Restart as the same deployment
            string deploymentId = DeploymentId;
            
            StopDefaultSilos();

            DeploymentId = deploymentId;
            primarySiloOptions.PickNewDeploymentId = false;
            secondarySiloOptions.PickNewDeploymentId = false;
            
            Primary = _StartOrleansSilo(Silo.SiloType.Primary, primarySiloOptions);
            Secondary = _StartOrleansSilo(Silo.SiloType.Secondary, secondarySiloOptions);
            WaitForLivenessToStabilize();
            Orleans.ActorClient.Initialize();
        }

        /// <summary>
        /// Do a semi-graceful Stop of the specified silo.
        /// </summary>
        /// <param name="instance">Silo to be stopped.</param>
        public void StopSilo(SiloHandle instance)
        {
            if (instance != null)
            {
                _StopOrleansSilo(instance, false);
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
                _StopOrleansSilo(instance, true);
            }
        }

        /// <summary>
        /// Do a Stop or Kill of the specified silo, followed by a restart.
        /// </summary>
        /// <param name="instance">Silo to be restarted.</param>
        /// <param name="kill">Whether the silo should be immediately Killed, or graceful Stop.</param>
        public SiloHandle RestartSilo(SiloHandle instance, bool kill = false)
        {
            if (instance != null)
            {
                var options = instance.Options;
                var type = instance.Silo.Type;
                _StopOrleansSilo(instance, kill);
                instance = _StartOrleansSilo(type, options);
                return instance;
            }
            return null;
        }
        
        #region Private methods
        private void _Initialize(UnitTestSiloOptions options, UnitTestClientOptions clientOptions = null)
        {
            bool doStartPrimary = false;
            bool doStartSecondary = false;

            if (options.StartFreshOrleans)
            {
                // the previous test was !startFresh, so we need to cleanup after it.
                if (Primary != null || Secondary != null || GrainClient.Current != null)
                {
                    StopDefaultSilos();
                }

                StopAdditionalSilos();

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
            }
            if (options.PickNewDeploymentId && String.IsNullOrEmpty(DeploymentId))
            {
                DeploymentId = _GetDeploymentId();
            }

            if (doStartPrimary)
            {
                Primary = _StartOrleansSilo(Silo.SiloType.Primary, options);
            }
            if (doStartSecondary)
            {
                Secondary = _StartOrleansSilo(Silo.SiloType.Secondary, options);
            }

            if (GrainClient.Current == null && options.StartClient)
            {
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
                    if (clientOptions.ProxiedGateway && clientOptions.Gateways != null)
                    {
                        clientConfig.Gateways = clientOptions.Gateways;
                        if (clientOptions.PreferedGatewayIndex >= 0)
                            clientConfig.PreferedGatewayIndex = clientOptions.PreferedGatewayIndex;
                    }
                    clientConfig.PropagateActivityId = clientOptions.PropagateActivityId;
                    if (!String.IsNullOrEmpty(DeploymentId))
                        clientConfig.DeploymentId = DeploymentId;
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
                Orleans.ActorClient.Initialize(clientConfig);
            }
        }

        private SiloHandle _StartOrleansSilo(Silo.SiloType type, UnitTestSiloOptions options, AppDomain shared = null)
        {
            // Load initial config settings, then apply some overrides below.
            ClusterConfiguration config = new ClusterConfiguration();
            if (options.SiloConfigFile == null)
            {
                config.StandardLoad();
            }
            else
            {
                config.LoadFromFile(options.SiloConfigFile.FullName);
            }

            int basePort = options.BasePort >= 0 ? options.BasePort : BasePort;

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

            if (!String.IsNullOrEmpty(DeploymentId))
            {
                config.Globals.DeploymentId = DeploymentId;
            }
            config.Defaults.PropagateActivityId = options.PropagateActivityId;
            if (options.LargeMessageWarningThreshold > 0)
            {
                config.Defaults.LargeMessageWarningThreshold = options.LargeMessageWarningThreshold;
            }

            config.Globals.LivenessType = options.LivenessType;

            globalConfig = config.Globals;

            string siloName;
            switch (type)
            {
                case Silo.SiloType.Primary:
                    siloName = "Primary";
                    break;
                default:
                    siloName = "Secondary_" + InstanceCounter.ToString(CultureInfo.InvariantCulture);
                    break;
            }

            NodeConfiguration nodeConfig = config.GetConfigurationForNode(siloName);
            nodeConfig.HostNameOrIPAddress = "loopback";
            nodeConfig.Port = basePort + InstanceCounter;
            nodeConfig.DefaultTraceLevel = config.Defaults.DefaultTraceLevel;
            nodeConfig.PropagateActivityId = config.Defaults.PropagateActivityId;
            nodeConfig.BulkMessageLimit = config.Defaults.BulkMessageLimit;

            config.Globals.ExpectedClusterSize = 2;

            config.Overrides[siloName] = nodeConfig;

            InstanceCounter++;

            Console.WriteLine("Starting a new silo in app domain {0} with config {1}", siloName, config.ToString(siloName));
            AppDomain appDomain;
            Silo silo = _LoadSiloInNewAppDomain(siloName, type, config, out appDomain);

            silo.Start();
            
            SiloHandle retValue = new SiloHandle
            {
                Name = siloName,
                Silo = silo,
                Options = options,
                Endpoint = silo.SiloAddress.Endpoint,
                AppDomain = appDomain,
            };
            return retValue;
        }

        private void _StopOrleansSilo(SiloHandle instance, bool kill)
        {
            if (!kill)
            {
                try { if (instance.Silo != null) instance.Silo.Stop(); }
                catch (RemotingException re) { Console.WriteLine(re); /* Ignore error */ }
                catch (Exception exc) { Console.WriteLine(exc); throw; }
            }

            try
            {
                _UnloadSiloInAppDomain(instance.AppDomain);
            }
            catch (Exception exc) { Console.WriteLine(exc); throw; }

            instance.AppDomain = null;
            instance.Silo = null;
            instance.Process = null;
        }

        private Silo _LoadSiloInNewAppDomain(string siloName, Silo.SiloType type, ClusterConfiguration config, out AppDomain appDomain)
        {
            var setup = new AppDomainSetup { ApplicationBase = Environment.CurrentDirectory };
            
            appDomain = AppDomain.CreateDomain(siloName, null, setup);
        
            var args = new object[] { siloName, type, config };

            var silo = (Silo) appDomain.CreateInstanceFromAndUnwrap(
                "OrleansRuntime.dll", typeof(Silo).FullName, false,
                BindingFlags.Default, null, args, CultureInfo.CurrentCulture,
                new object[] { });

            appDomain.UnhandledException += _ReportUnobservedException;

            return silo;
        }

        private static void _UnloadSiloInAppDomain(AppDomain appDomain)
        {
            if (appDomain != null)
            {
                appDomain.UnhandledException -= _ReportUnobservedException;
                AppDomain.Unload(appDomain);
            }
        }

        private string _GetDeploymentId()
        {
            if (!String.IsNullOrEmpty(DeploymentId))
            {
                return DeploymentId;
            }
            string prefix = DeploymentIdPrefix ?? "testdepid-";
            int randomSuffix = random.Next(1000);
            DateTime now = DateTime.UtcNow;
            string DateTimeFormat = "yyyy-MM-dd-hh-mm-ss-fff";
            string depId = String.Format("{0}{1}-{2}",
                prefix, now.ToString(DateTimeFormat, CultureInfo.InvariantCulture), randomSuffix);
            return depId;
        }

        private static TimeSpan _Multiply(TimeSpan time, double value)
        {
            double ticksD = checked(time.Ticks * value);
            long ticks = checked((long)ticksD);
            return TimeSpan.FromTicks(ticks);
        }

        private static void _ReportUnobservedException(object sender, UnhandledExceptionEventArgs eventArgs)
        {
            Exception exception = (Exception)eventArgs.ExceptionObject;
            Console.WriteLine("Unobserved exception: {0}", exception);
        }
        #endregion
    }
}
