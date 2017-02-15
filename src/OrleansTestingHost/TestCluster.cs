using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Streams;
using Orleans.TestingHost.Utils;

namespace Orleans.TestingHost
{

    /// <summary>
    /// A host class for local testing with Orleans using in-process silos. 
    /// Runs a Primary and optionally secondary silos in separate app domains, and client in the main app domain.
    /// Additional silos can also be started in-process on demand if required for particular test cases.
    /// </summary>
    /// <remarks>
    /// Make sure that your test project references your test grains and test grain interfaces 
    /// projects, and has CopyLocal=True set on those references [which should be the default].
    /// </remarks>
    public class TestCluster
    {
        /// <summary>
        /// Primary silo handle
        /// </summary>
        public SiloHandle Primary { get; private set; }

        /// <summary>
        /// List of handles to the secondary silos
        /// </summary>
        public IReadOnlyList<SiloHandle> SecondarySilos => this.additionalSilos;

        private readonly List<SiloHandle> additionalSilos = new List<SiloHandle>();

        private readonly Dictionary<string, GeneratedAssembly> additionalAssemblies = new Dictionary<string, GeneratedAssembly>();

        /// <summary>
        /// Client configuration to use when initializing the client
        /// </summary>
        public ClientConfiguration ClientConfiguration { get; private set; }

        /// <summary>
        /// Cluster configuration
        /// </summary>
        public ClusterConfiguration ClusterConfiguration { get; private set; }

        private readonly StringBuilder log = new StringBuilder();

        /// <summary>
        /// DeploymentId of the cluster
        /// </summary>
        public string DeploymentId => this.ClusterConfiguration.Globals.DeploymentId;

        /// <summary>
        /// GrainFactory to use in the tests
        /// </summary>
        public IGrainFactory GrainFactory { get; private set; }

        /// <summary>
        /// The client-side <see cref="StreamProviderManager"/>.
        /// </summary>
        public IStreamProviderManager StreamProviderManager { get; private set; }

        /// <summary>
        /// GrainFactory to use in the tests
        /// </summary>
        internal IInternalGrainFactory InternalGrainFactory { get; private set; }
        
        /// <summary>
        /// Configure the default Primary test silo, plus client in-process.
        /// </summary>
        public TestCluster()
            : this(new TestClusterOptions())
        {
        }

        /// <summary>
        /// Configures the test cluster plus client in-process.
        /// </summary>
        public TestCluster(TestClusterOptions options)
            : this(options.ClusterConfiguration, options.ClientConfiguration)
        {
        }

        /// <summary>
        /// Configures the test cluster plus default client in-process.
        /// </summary>
        public TestCluster(ClusterConfiguration clusterConfiguration)
            : this(clusterConfiguration, TestClusterOptions.BuildClientConfiguration(clusterConfiguration))
        {
        }

        /// <summary>
        /// Configures the test cluster plus client in-process,
        /// using the specified silo and client config configurations.
        /// </summary>
        public TestCluster(ClusterConfiguration clusterConfiguration, ClientConfiguration clientConfiguration)
        {
            this.ClusterConfiguration = clusterConfiguration;
            this.ClientConfiguration = clientConfiguration;
        }

        /// <summary>
        /// Deploys the cluster using the specified configuration and starts the client in-process.
        /// It will start all the silos defined in the <see cref="Runtime.Configuration.ClusterConfiguration.Overrides"/> collection.
        /// </summary>
        public void Deploy()
        {
            this.Deploy(this.ClusterConfiguration.Overrides.Keys);
        }

        /// <summary>
        /// Deploys the cluster using the specified configuration and starts the client in-process.
        /// </summary>
        /// <param name="siloNames">Only deploy the specified silos which must also be present in the <see cref="Runtime.Configuration.ClusterConfiguration.Overrides"/> collection.</param>
        public void Deploy(IEnumerable<string> siloNames)
        {
            try
            {
                DeployAsync(siloNames).Wait();
            }
            catch (AggregateException ex)
            {
                if (ex.InnerExceptions.Count > 1) throw;
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            }
        }

        /// <summary>
        /// Deploys the cluster using the specified configuration and starts the client in-process.
        /// </summary>
        /// <param name="siloNames">Only deploy the specified silos which must also be present in the <see cref="Runtime.Configuration.ClusterConfiguration.Overrides"/> collection.</param>
        public async Task DeployAsync(IEnumerable<string> siloNames)
        {
            if (Primary != null) throw new InvalidOperationException("Cluster host already deployed.");

            AppDomain.CurrentDomain.UnhandledException += ReportUnobservedException;

            try
            {
                string startMsg = "----------------------------- STARTING NEW UNIT TEST SILO HOST: " + GetType().FullName + " -------------------------------------";
                WriteLog(startMsg);
                await InitializeAsync(siloNames);
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
                //throw new Exception(
                //    string.Format("Exception during test initialization: {0}",
                //        LogFormatter.PrintException(baseExc)));
                throw;
            }
        }

        /// <summary>
        /// Get the list of current active silos.
        /// </summary>
        /// <returns>List of current silos.</returns>
        public IEnumerable<SiloHandle> GetActiveSilos()
        {
            WriteLog("GetActiveSilos: Primary={0} + {1} Additional={2}",
                Primary, additionalSilos.Count, Runtime.Utils.EnumerableToString(additionalSilos));

            if (Primary?.IsActive == true) yield return Primary;
            if (additionalSilos.Count > 0)
                foreach (var s in additionalSilos)
                    if (s?.IsActive == true)
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
            var ret = activeSilos.FirstOrDefault(s => s.SiloAddress.Equals(siloAddress));
            return ret;
        }

        /// <summary>
        /// Wait for the silo liveness sub-system to detect and act on any recent cluster membership changes.
        /// </summary>
        /// <param name="didKill">Whether recent membership changes we done by graceful Stop.</param>
        public async Task WaitForLivenessToStabilizeAsync(bool didKill = false)
        {
            TimeSpan stabilizationTime = GetLivenessStabilizationTime(this.ClusterConfiguration.Globals, didKill);
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
        /// Start an additional silo, so that it joins the existing cluster.
        /// </summary>
        /// <returns>SiloHandle for the newly started silo.</returns>
        public SiloHandle StartAdditionalSilo()
        {
            var clusterConfig = this.ClusterConfiguration;
            short instanceNumber = (short)clusterConfig.Overrides.Count;
            var defaultNode = clusterConfig.Defaults;
            int baseSiloPort = defaultNode.Port;
            int baseGatewayPort = defaultNode.ProxyGatewayEndpoint.Port;
            var nodeConfig = TestClusterOptions.AddNodeConfiguration(
                this.ClusterConfiguration, 
                Silo.SiloType.Secondary,
                instanceNumber, 
                baseSiloPort, 
                baseGatewayPort);

            SiloHandle instance = StartOrleansSilo(
                Silo.SiloType.Secondary,
                this.ClusterConfiguration,
                nodeConfig);
            additionalSilos.Add(instance);
            return instance;
        }

        /// <summary>
        /// Start a number of additional silo, so that they join the existing cluster.
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
        /// Stop any additional silos, not including the default Primary silo.
        /// </summary>
        public void StopSecondarySilos()
        {
            foreach (SiloHandle instance in this.additionalSilos.ToList())
            {
                StopSilo(instance);
            }
        }

        /// <summary>
        /// Stops the default Primary silo.
        /// </summary>
        public void StopPrimarySilo()
        {
            try
            {
                GrainClient.Uninitialize();
            }
            catch (Exception exc) { WriteLog("Exception Uninitializing grain client: {0}", exc); }

            StopSilo(Primary);
        }

        /// <summary>
        /// Stop all current silos.
        /// </summary>
        public void StopAllSilos()
        {
            StopSecondarySilos();
            StopPrimarySilo();
            AppDomain.CurrentDomain.UnhandledException -= ReportUnobservedException;
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
                if (Primary == instance)
                {
                    Primary = null;
                }
                else
                {
                    additionalSilos.Remove(instance);
                }
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
        /// Performs a hard kill on client.  Client will not cleanup resources.
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
                var type = instance.Type;
                var siloName = instance.Name;
                StopSilo(instance);
                var newInstance = StartOrleansSilo(type, this.ClusterConfiguration, this.ClusterConfiguration.Overrides[siloName]);

                if (type == Silo.SiloType.Primary && siloName == Silo.PrimarySiloName)
                {
                    Primary = newInstance;
                }
                else
                {
                    additionalSilos.Add(newInstance);
                }

                return newInstance;
            }

            return null;
        }

        /// <summary>
        /// Restart a previously stopped.
        /// </summary>
        /// <param name="siloName">Silo to be restarted.</param>
        public SiloHandle RestartStoppedSecondarySilo(string siloName)
        {
            if (siloName == null) throw new ArgumentNullException(nameof(siloName));
            var newInstance = StartOrleansSilo(Silo.SiloType.Secondary, this.ClusterConfiguration, this.ClusterConfiguration.Overrides[siloName]);
            additionalSilos.Add(newInstance);
            return newInstance;
        }

        #region Private methods

        /// <summary>
        /// Initialize the grain client. This should be already done by <see cref="Deploy()"/> or <see cref="DeployAsync"/>
        /// </summary>
        public void InitializeClient()
        {
            WriteLog("Initializing Grain Client");
            ClientConfiguration clientConfig = this.ClientConfiguration;

            if (Debugger.IsAttached)
            {
                // Test is running inside debugger - Make timeout ~= infinite
                clientConfig.ResponseTimeout = TimeSpan.FromMilliseconds(1000000);
            }

            GrainClient.Initialize(clientConfig);
            this.GrainFactory = GrainClient.GrainFactory;
            this.InternalGrainFactory = this.GrainFactory as IInternalGrainFactory;
            this.StreamProviderManager = RuntimeClient.Current.CurrentStreamProviderManager;
        }
        
        private async Task InitializeAsync(IEnumerable<string> siloNames)
        {
            var silos = siloNames.ToList();
            foreach (var siloName in silos)
            {
                if (!this.ClusterConfiguration.Overrides.Keys.Contains(siloName))
                {
                    throw new ArgumentOutOfRangeException(nameof(siloNames), $"Silo name {siloName} does not exist in the ClusterConfiguration.Overrides collection");
                }
            }

            if (silos.Contains(Silo.PrimarySiloName))
            {
                Primary = StartOrleansSilo(Silo.SiloType.Primary, this.ClusterConfiguration, this.ClusterConfiguration.Overrides[Silo.PrimarySiloName]);
            }

            var secondarySiloNames = silos.Where(name => !string.Equals(Silo.PrimarySiloName, name)).ToList();
            if (secondarySiloNames.Count > 0)
            {
                var siloStartTasks = secondarySiloNames.Select(siloName =>
                {
                    return Task.Run(() => StartOrleansSilo(Silo.SiloType.Secondary, this.ClusterConfiguration, this.ClusterConfiguration.Overrides[siloName]));
                }).ToList();

                try
                {
                    await Task.WhenAll(siloStartTasks);
                }
                catch (Exception)
                {
                    this.additionalSilos.AddRange(siloStartTasks.Where(t => t.Exception == null).Select(t => t.Result));
                    throw;
                }

                this.additionalSilos.AddRange(siloStartTasks.Select(t => t.Result));
            }

            WriteLog("Done initializing cluster");

            if (this.ClientConfiguration != null)
            {
                InitializeClient();
            }
        }

        private SiloHandle StartOrleansSilo(Silo.SiloType type, ClusterConfiguration clusterConfig, NodeConfiguration nodeConfig)
        {
            return StartOrleansSilo(this, type, clusterConfig, nodeConfig);
        }

        /// <summary>
        /// Start a new silo in the target cluster
        /// </summary>
        /// <param name="cluster">The TestCluster in which the silo should be deployed</param>
        /// <param name="type">The type of the silo to deploy</param>
        /// <param name="clusterConfig">The cluster config to use</param>
        /// <param name="nodeConfig">The configuration for the silo to deploy</param>
        /// <returns>A handle to the silo deployed</returns>
        public static SiloHandle StartOrleansSilo(TestCluster cluster, Silo.SiloType type, ClusterConfiguration clusterConfig, NodeConfiguration nodeConfig)
        {
            if (cluster == null) throw new ArgumentNullException(nameof(cluster));
            var siloName = nodeConfig.SiloName;

            cluster.WriteLog("Starting a new silo in app domain {0} with config {1}", siloName, clusterConfig.ToString(siloName));
            var handle = cluster.LoadSiloInNewAppDomain(siloName, type, clusterConfig, nodeConfig);
            return handle;
        }

        private void StopOrleansSilo(SiloHandle instance, bool stopGracefully)
        {
            instance.StopSilo(stopGracefully);
            instance.Dispose();
        }

        private SiloHandle LoadSiloInNewAppDomain(string siloName, Silo.SiloType type, ClusterConfiguration config, NodeConfiguration nodeConfiguration)
        {
            return AppDomainSiloHandle.Create(siloName, type, config, nodeConfiguration, this.additionalAssemblies);
        }

        #endregion

        #region Tracing helper functions

        private static void ReportUnobservedException(object sender, UnhandledExceptionEventArgs eventArgs)
        {
            Exception exception = (Exception)eventArgs.ExceptionObject;
            // WriteLog("Unobserved exception: {0}", exception);
        }

        private void WriteLog(string format, params object[] args)
        {
            log.AppendFormat(format + Environment.NewLine, args);
        }

        private void FlushLogToConsole()
        {
            Console.WriteLine(log.ToString());
        }

        private void WriteLog(object value)
        {
            WriteLog(value.ToString());
        }

        #endregion
    }
}
