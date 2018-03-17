using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.TestingHost.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Orleans.Configuration;
using Microsoft.Extensions.Options;

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
        private readonly List<SiloHandle> additionalSilos = new List<SiloHandle>();
        private readonly TestClusterOptions options;
        private readonly StringBuilder log = new StringBuilder();
        private int startedInstances;

        /// <summary>
        /// Primary silo handle, if applicable.
        /// </summary>
        /// <remarks>This handle is valid only when using Grain-based membership.</remarks>
        public SiloHandle Primary { get; private set; }

        /// <summary>
        /// List of handles to the secondary silos.
        /// </summary>
        public IReadOnlyList<SiloHandle> SecondarySilos => this.additionalSilos;

        /// <summary>
        /// Collection of all known silos.
        /// </summary>
        public ReadOnlyCollection<SiloHandle> Silos
        {
            get
            {
                var result = new List<SiloHandle>();
                if (this.Primary != null)
                {
                    result.Add(this.Primary);
                }

                result.AddRange(this.additionalSilos);

                return result.AsReadOnly();
            }
        }

        /// <summary>
        /// Options used to configure the test cluster.
        /// </summary>
        /// <remarks>This is the options you configured your test cluster with, or the default one. 
        /// If the cluster is being configured via ClusterConfiguration, then this object may not reflect the true settings.
        /// </remarks>
        public TestClusterOptions Options => this.options;

        /// <summary>
        /// The internal client interface.
        /// </summary>
        internal IInternalClusterClient InternalClient { get; private set; }

        /// <summary>
        /// The client.
        /// </summary>
        public IClusterClient Client => this.InternalClient;

        /// <summary>
        /// GrainFactory to use in the tests
        /// </summary>
        public IGrainFactory GrainFactory => this.Client;

        /// <summary>
        /// GrainFactory to use in the tests
        /// </summary>
        internal IInternalGrainFactory InternalGrainFactory => this.InternalClient;

        /// <summary>
        /// Client-side <see cref="IServiceProvider"/> to use in the tests.
        /// </summary>
        public IServiceProvider ServiceProvider => this.Client.ServiceProvider;
        
        /// <summary>
        /// SerializationManager to use in the tests
        /// </summary>
        public SerializationManager SerializationManager { get; private set; }

        /// <summary>
        /// Delegate used to create and start an individual silo.
        /// </summary>
        public Func<string, IList<IConfigurationSource>, SiloHandle> CreateSilo { private get; set; } = InProcessSiloHandle.Create;
        
        /// <summary>
        /// Configures the test cluster plus client in-process.
        /// </summary>
        public TestCluster(TestClusterOptions options, IReadOnlyList<IConfigurationSource> configurationSources)
        {
            this.options = options;
            this.ConfigurationSources = configurationSources.ToArray();
        }

        /// <summary>
        /// Deploys the cluster using the specified configuration and starts the client in-process.
        /// It will start the number of silos defined in <see cref="TestClusterOptions.InitialSilosCount"/>.
        /// </summary>
        public void Deploy()
        {
            this.DeployAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        /// Deploys the cluster using the specified configuration and starts the client in-process.
        /// </summary>
        public async Task DeployAsync()
        {
            if (this.Primary != null || this.additionalSilos.Count > 0) throw new InvalidOperationException("Cluster host already deployed.");

            AppDomain.CurrentDomain.UnhandledException += ReportUnobservedException;

            try
            {
                string startMsg = "----------------------------- STARTING NEW UNIT TEST SILO HOST: " + GetType().FullName + " -------------------------------------";
                WriteLog(startMsg);
                await InitializeAsync();
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
            var activeSilos = GetActiveSilos().ToList();
            var ret = activeSilos.FirstOrDefault(s => s.SiloAddress.Equals(siloAddress));
            return ret;
        }

        /// <summary>
        /// Wait for the silo liveness sub-system to detect and act on any recent cluster membership changes.
        /// </summary>
        /// <param name="didKill">Whether recent membership changes we done by graceful Stop.</param>
        public async Task WaitForLivenessToStabilizeAsync(bool didKill = false)
        {
            var clusterMembershipOptions = this.ServiceProvider.GetService<IOptions<ClusterMembershipOptions>>().Value;
            TimeSpan stabilizationTime = GetLivenessStabilizationTime(clusterMembershipOptions, didKill);
            WriteLog(Environment.NewLine + Environment.NewLine + "WaitForLivenessToStabilize is about to sleep for {0}", stabilizationTime);
            await Task.Delay(stabilizationTime);
            WriteLog("WaitForLivenessToStabilize is done sleeping");
        }

        /// <summary>
        /// Get the timeout value to use to wait for the silo liveness sub-system to detect and act on any recent cluster membership changes.
        /// <seealso cref="WaitForLivenessToStabilizeAsync"/>
        /// </summary>
        public static TimeSpan GetLivenessStabilizationTime(ClusterMembershipOptions clusterMembershipOptions, bool didKill = false)
        {
            TimeSpan stabilizationTime = TimeSpan.Zero;
            if (didKill)
            {
                // in case of hard kill (kill and not Stop), we should give silos time to detect failures first.
                stabilizationTime = TestingUtils.Multiply(clusterMembershipOptions.ProbeTimeout, clusterMembershipOptions.NumMissedProbesLimit);
            }
            if (clusterMembershipOptions.UseLivenessGossip)
            {
                stabilizationTime += TimeSpan.FromSeconds(5);
            }
            else
            {
                stabilizationTime += TestingUtils.Multiply(clusterMembershipOptions.TableRefreshTimeout, 2);
            }
            return stabilizationTime;
        }

        /// <summary>
        /// Start an additional silo, so that it joins the existing cluster.
        /// </summary>
        /// <returns>SiloHandle for the newly started silo.</returns>
        public SiloHandle StartAdditionalSilo(bool startAdditionalSiloOnNewPort = false)
        {
            return this.StartAdditionalSilos(1, startAdditionalSiloOnNewPort).GetAwaiter().GetResult().Single();
        }

        /// <summary>
        /// Start a number of additional silo, so that they join the existing cluster.
        /// </summary>
        /// <param name="silosToStart">Number of silos to start.</param>
        /// <param name="startAdditionalSiloOnNewPort"></param>
        /// <returns>List of SiloHandles for the newly started silos.</returns>
        public async Task<List<SiloHandle>> StartAdditionalSilos(int silosToStart, bool startAdditionalSiloOnNewPort = false)
        {
            var instances = new List<SiloHandle>();
            if (silosToStart > 0)
            {
                var siloStartTasks = Enumerable.Range(this.startedInstances, silosToStart)
                    .Select(instanceNumber => Task.Run(() => StartOrleansSilo((short)instanceNumber, this.options, startSiloOnNewPort: startAdditionalSiloOnNewPort))).ToArray();

                try
                {
                    await Task.WhenAll(siloStartTasks);
                }
                catch (Exception)
                {
                    this.additionalSilos.AddRange(siloStartTasks.Where(t => t.Exception == null).Select(t => t.Result));
                    throw;
                }

                instances.AddRange(siloStartTasks.Select(t => t.Result));
                this.additionalSilos.AddRange(instances);
            }

            return instances;
        }

        /// <summary>
        /// Stop any additional silos, not including the default Primary silo.
        /// </summary>
        public void StopSecondarySilos()
        {
            foreach (var instance in this.additionalSilos.ToList())
            {
                StopSilo(instance);
            }
        }

        /// <summary>
        /// Stops the default Primary silo.
        /// </summary>
        public void StopPrimarySilo()
        {
            if (Primary == null) throw new InvalidOperationException("There is no primary silo");
            StopClusterClient();
            StopSilo(Primary);
        }

        private void StopClusterClient()
        {
            try
            {
                this.InternalClient?.Close().GetAwaiter().GetResult();
            }
            catch (Exception exc)
            {
                WriteLog("Exception Uninitializing grain client: {0}", exc);
            }
            finally
            {
                this.InternalClient?.Dispose();
                this.InternalClient = null;
            }
        }

        /// <summary>
        /// Stop all current silos.
        /// </summary>
        public void StopAllSilos()
        {
            StopClusterClient();
            StopSecondarySilos();
            if (Primary != null)
            {
                StopPrimarySilo();
            }
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
            this.InternalClient?.Abort();
            this.InternalClient = null;
        }

        /// <summary>
        /// Do a Stop or Kill of the specified silo, followed by a restart.
        /// </summary>
        /// <param name="instance">Silo to be restarted.</param>
        public SiloHandle RestartSilo(SiloHandle instance)
        {
            if (instance != null)
            {
                var instanceNumber = instance.InstanceNumber;
                var siloName = instance.Name;
                StopSilo(instance);
                var newInstance = StartOrleansSilo(instanceNumber, this.options);

                if (siloName == Silo.PrimarySiloName)
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
            var siloHandle = this.Silos.Single(s => s.Name.Equals(siloName, StringComparison.Ordinal));
            var newInstance = this.StartOrleansSilo(this.Silos.IndexOf(siloHandle), this.options);
            additionalSilos.Add(newInstance);
            return newInstance;
        }

        #region Private methods

        /// <summary>
        /// Initialize the grain client. This should be already done by <see cref="Deploy()"/> or <see cref="DeployAsync"/>
        /// </summary>
        public void InitializeClient()
        {
            WriteLog("Initializing Cluster Client");

            this.InternalClient = (IInternalClusterClient)TestClusterHostFactory.CreateClusterClient("MainClient", this.ConfigurationSources);
            this.InternalClient.Connect().GetAwaiter().GetResult();
            this.SerializationManager = this.ServiceProvider.GetRequiredService<SerializationManager>();
        }

        public IReadOnlyList<IConfigurationSource> ConfigurationSources { get; }

        private async Task InitializeAsync()
        {
            short silosToStart = this.options.InitialSilosCount;

            if (this.options.UseTestClusterMembership)
            {
                this.Primary = StartOrleansSilo(this.startedInstances, this.options);
                silosToStart--;
            }

            if (silosToStart > 0)
            {
                await this.StartAdditionalSilos(silosToStart);
            }

            WriteLog("Done initializing cluster");

            if (this.options.InitializeClientOnDeploy)
            {
                InitializeClient();
            }
        }
        
        /// <summary>
        /// Start a new silo in the target cluster
        /// </summary>
        /// <param name="cluster">The TestCluster in which the silo should be deployed</param>
        /// <param name="instanceNumber">The instance number to deploy</param>
        /// <param name="clusterOptions">The options to use.</param>
        /// <param name="configurationOverrides">Configuration overrides.</param>
        /// <param name="startSiloOnNewPort">Whether we start this silo on a new port, instead of the default one</param>
        /// <returns>A handle to the silo deployed</returns>
        public static SiloHandle StartOrleansSilo(TestCluster cluster, int instanceNumber, TestClusterOptions clusterOptions, IReadOnlyList<IConfigurationSource> configurationOverrides = null, bool startSiloOnNewPort = false)
        {
            if (cluster == null) throw new ArgumentNullException(nameof(cluster));
            return cluster.StartOrleansSilo(instanceNumber, clusterOptions, configurationOverrides, startSiloOnNewPort);
        }

        /// <summary>
        /// Starts a new silo.
        /// </summary>
        /// <param name="instanceNumber">The instance number to deploy</param>
        /// <param name="clusterOptions">The options to use.</param>
        /// <param name="configurationOverrides">Configuration overrides.</param>
        /// <param name="startSiloOnNewPort">Whether we start this silo on a new port, instead of the default one</param>
        /// <returns>A handle to the deployed silo.</returns>
        public SiloHandle StartOrleansSilo(int instanceNumber, TestClusterOptions clusterOptions, IReadOnlyList<IConfigurationSource> configurationOverrides = null, bool startSiloOnNewPort = false)
        {
            var configurationSources = this.ConfigurationSources.ToList();

            // Add overrides.
            if (configurationOverrides != null) configurationSources.AddRange(configurationOverrides);
            var siloSpecificOptions = TestSiloSpecificOptions.Create(clusterOptions, instanceNumber, startSiloOnNewPort);
            configurationSources.Add(new MemoryConfigurationSource
            {
                InitialData = siloSpecificOptions.ToDictionary()
            });

            var handle = this.CreateSilo(siloSpecificOptions.SiloName,configurationSources);
            handle.InstanceNumber = (short)instanceNumber;
            Interlocked.Increment(ref this.startedInstances);
            return handle;
        }

        private void StopOrleansSilo(SiloHandle instance, bool stopGracefully)
        {
            try
            {
                instance.StopSilo(stopGracefully);
                instance.Dispose();
            }
            finally
            {
                Interlocked.Decrement(ref this.startedInstances);
            }
        }

        #endregion

        #region Tracing helper functions

        public string GetLog()
        {
            return this.log.ToString();
        }

        private void ReportUnobservedException(object sender, UnhandledExceptionEventArgs eventArgs)
        {
            Exception exception = (Exception)eventArgs.ExceptionObject;
            this.WriteLog("Unobserved exception: {0}", exception);
        }

        private void WriteLog(string format, params object[] args)
        {
            log.AppendFormat(format + Environment.NewLine, args);
        }

        private void FlushLogToConsole()
        {
            Console.WriteLine(GetLog());
        }

        #endregion
    }
}
