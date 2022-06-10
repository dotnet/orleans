using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.TestingHost.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Orleans.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using Orleans.TestingHost.InMemoryTransport;
using Orleans.TestingHost.UnixSocketTransport;

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
    public class TestCluster : IDisposable, IAsyncDisposable
    {
        private readonly List<SiloHandle> additionalSilos = new List<SiloHandle>();
        private readonly TestClusterOptions options;
        private readonly StringBuilder log = new StringBuilder();
        private readonly InMemoryTransportConnectionHub _transportHub = new();
        private bool _disposed;
        private int startedInstances;

        /// <summary>
        /// Primary silo handle, if applicable.
        /// </summary>
        /// <remarks>This handle is valid only when using Grain-based membership.</remarks>
        public SiloHandle Primary { get; private set; }

        /// <summary>
        /// List of handles to the secondary silos.
        /// </summary>
        public IReadOnlyList<SiloHandle> SecondarySilos
        {
            get
            {
                lock (this.additionalSilos)
                {
                    return new List<SiloHandle>(this.additionalSilos);
                }
            }
        }

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

                lock (this.additionalSilos)
                {
                    result.AddRange(this.additionalSilos);
                }

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
        internal IHost ClientHost { get; private set; }

        /// <summary>
        /// The internal client interface.
        /// </summary>
        internal IInternalClusterClient InternalClient => ClientHost?.Services.GetRequiredService<IInternalClusterClient>();

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
        /// Delegate used to create and start an individual silo.
        /// </summary>
        public Func<string, IConfiguration, Task<SiloHandle>> CreateSiloAsync { private get; set; }

        /// <summary>
        /// The port allocator.
        /// </summary>
        public ITestClusterPortAllocator PortAllocator { get; }
        
        /// <summary>
        /// Configures the test cluster plus client in-process.
        /// </summary>
        public TestCluster(
            TestClusterOptions options,
            IReadOnlyList<IConfigurationSource> configurationSources,
            ITestClusterPortAllocator portAllocator)
        {
            this.options = options;
            this.ConfigurationSources = configurationSources.ToArray();
            this.PortAllocator = portAllocator;
            this.CreateSiloAsync = DefaultCreateSiloAsync;
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

                if (this.options.InitializeClientOnDeploy)
                {
                    await WaitForInitialStabilization();
                }
            }
            catch (TimeoutException te)
            {
                FlushLogToConsole();
                throw new TimeoutException("Timeout during test initialization", te);
            }
            catch (Exception ex)
            {
                await StopAllSilosAsync();

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

        private async Task WaitForInitialStabilization()
        {
            // Poll each silo to check that it knows the expected number of active silos.
            // If any silo does not have the expected number of active silos in its cluster membership oracle, try again.
            // If the cluster membership has not stabilized after a certain period of time, give up and continue anyway.
            var totalWait = Stopwatch.StartNew();
            while (true)
            {
                var silos = this.Silos;
                var expectedCount = silos.Count;
                var remainingSilos = expectedCount;

                foreach (var silo in silos)
                {
                    var hooks = this.InternalClient.GetTestHooks(silo);
                    var statuses = await hooks.GetApproximateSiloStatuses();
                    var activeCount = statuses.Count(s => s.Value == SiloStatus.Active);
                    if (activeCount != expectedCount) break;
                    remainingSilos--;
                }

                if (remainingSilos == 0)
                {
                    totalWait.Stop();
                    break;
                }

                WriteLog($"{remainingSilos} silos do not have a consistent cluster view, waiting until stabilization.");
                await Task.Delay(TimeSpan.FromMilliseconds(100));

                if (totalWait.Elapsed < TimeSpan.FromSeconds(60))
                {
                    WriteLog($"Warning! {remainingSilos} silos do not have a consistent cluster view after {totalWait.ElapsedMilliseconds}ms, continuing without stabilization.");
                    break;
                }
            }
        }

        /// <summary>
        /// Get the list of current active silos.
        /// </summary>
        /// <returns>List of current silos.</returns>
        public IEnumerable<SiloHandle> GetActiveSilos()
        {
            var additional = new List<SiloHandle>();
            lock (additionalSilos)
            {
                additional.AddRange(additionalSilos);
            }

            WriteLog("GetActiveSilos: Primary={0} + {1} Additional={2}",
                Primary, additional.Count, Runtime.Utils.EnumerableToString(additional));

            if (Primary?.IsActive == true) yield return Primary;
            

            if (additional.Count > 0)
            foreach (var s in additional)
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
            var ret = activeSilos.Find(s => s.SiloAddress.Equals(siloAddress));
            return ret;
        }

        /// <summary>
        /// Wait for the silo liveness sub-system to detect and act on any recent cluster membership changes.
        /// </summary>
        /// <param name="didKill">Whether recent membership changes we done by graceful Stop.</param>
        public async Task WaitForLivenessToStabilizeAsync(bool didKill = false)
        {
            var clusterMembershipOptions = this.ServiceProvider.GetRequiredService<IOptions<ClusterMembershipOptions>>().Value;
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
            return StartAdditionalSiloAsync(startAdditionalSiloOnNewPort).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Start an additional silo, so that it joins the existing cluster.
        /// </summary>
        /// <returns>SiloHandle for the newly started silo.</returns>
        public async Task<SiloHandle> StartAdditionalSiloAsync(bool startAdditionalSiloOnNewPort = false)
        {
            return (await this.StartAdditionalSilosAsync(1, startAdditionalSiloOnNewPort)).Single();
        }

        /// <summary>
        /// Start a number of additional silo, so that they join the existing cluster.
        /// </summary>
        /// <param name="silosToStart">Number of silos to start.</param>
        /// <param name="startAdditionalSiloOnNewPort"></param>
        /// <returns>List of SiloHandles for the newly started silos.</returns>
        public async Task<List<SiloHandle>> StartAdditionalSilosAsync(int silosToStart, bool startAdditionalSiloOnNewPort = false)
        {
            var instances = new List<SiloHandle>();
            if (silosToStart > 0)
            {
                var siloStartTasks = Enumerable.Range(this.startedInstances, silosToStart)
                    .Select(instanceNumber => Task.Run(() => StartSiloAsync((short)instanceNumber, this.options, startSiloOnNewPort: startAdditionalSiloOnNewPort))).ToArray();

                try
                {
                    await Task.WhenAll(siloStartTasks);
                }
                catch (Exception)
                {
                    lock (additionalSilos)
                    {
                        this.additionalSilos.AddRange(siloStartTasks.Where(t => t.Exception == null).Select(t => t.Result));
                    }

                    throw;
                }

                instances.AddRange(siloStartTasks.Select(t => t.Result));
                lock (additionalSilos)
                {
                    this.additionalSilos.AddRange(instances);
                }
            }

            return instances;
        }

        /// <summary>
        /// Stop any additional silos, not including the default Primary silo.
        /// </summary>
        public async Task StopSecondarySilosAsync()
        {
            foreach (var instance in this.additionalSilos.ToList())
            {
                await StopSiloAsync(instance);
            }
        }

        /// <summary>
        /// Stops the default Primary silo.
        /// </summary>
        public async Task StopPrimarySiloAsync()
        {
            if (Primary == null) throw new InvalidOperationException("There is no primary silo");
            await StopClusterClientAsync();
            await StopSiloAsync(Primary);
        }

        /// <summary>
        /// Stop cluster client as an asynchronous operation.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        public async Task StopClusterClientAsync()
        {
            var client = this.ClientHost;
            try
            {
                if (client is not null)
                {
                    await client.StopAsync().ConfigureAwait(false);
                }                
            }
            catch (Exception exc)
            {
                WriteLog("Exception stopping client: {0}", exc);
            }
            finally
            {
                await DisposeAsync(client).ConfigureAwait(false);
                ClientHost = null;
            }
        }

        /// <summary>
        /// Stop all current silos.
        /// </summary>
        public void StopAllSilos()
        {
            StopAllSilosAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        /// Stop all current silos.
        /// </summary>
        public async Task StopAllSilosAsync()
        {
            await StopClusterClientAsync();
            await StopSecondarySilosAsync();
            if (Primary != null)
            {
                await StopPrimarySiloAsync();
            }
            AppDomain.CurrentDomain.UnhandledException -= ReportUnobservedException;
        }

        /// <summary>
        /// Do a semi-graceful Stop of the specified silo.
        /// </summary>
        /// <param name="instance">Silo to be stopped.</param>
        public async Task StopSiloAsync(SiloHandle instance)
        {
            if (instance != null)
            {
                await StopSiloAsync(instance, true);
                if (Primary == instance)
                {
                    Primary = null;
                }
                else
                {
                    lock (additionalSilos)
                    {
                        additionalSilos.Remove(instance);
                    }
                }
            }
        }

        /// <summary>
        /// Do an immediate Kill of the specified silo.
        /// </summary>
        /// <param name="instance">Silo to be killed.</param>
        public async Task KillSiloAsync(SiloHandle instance)
        {
            if (instance != null)
            {
                // do NOT stop, just kill directly, to simulate crash.
                await StopSiloAsync(instance, false);
                if (Primary == instance)
                {
                    Primary = null;
                }
                else
                {
                    lock (additionalSilos)
                    {
                        additionalSilos.Remove(instance);
                    }
                }
            }
        }

        /// <summary>
        /// Performs a hard kill on client.  Client will not cleanup resources.
        /// </summary>
        public async Task KillClientAsync()
        {
            var client = ClientHost;
            if (client != null)
            {
                var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                try
                {
                    await client.StopAsync(cancelled.Token).ConfigureAwait(false);
                }
                finally
                {
                    await DisposeAsync(client);
                    ClientHost = null;
                }
            }
        }

        /// <summary>
        /// Do a Stop or Kill of the specified silo, followed by a restart.
        /// </summary>
        /// <param name="instance">Silo to be restarted.</param>
        public async Task<SiloHandle> RestartSiloAsync(SiloHandle instance)
        {
            if (instance != null)
            {
                var instanceNumber = instance.InstanceNumber;
                var siloName = instance.Name;
                await StopSiloAsync(instance);
                var newInstance = await StartSiloAsync(instanceNumber, this.options);

                if (siloName == Silo.PrimarySiloName)
                {
                    Primary = newInstance;
                }
                else
                {
                    lock (additionalSilos)
                    {
                        additionalSilos.Add(newInstance);
                    }
                }

                return newInstance;
            }

            return null;
        }

        /// <summary>
        /// Restart a previously stopped.
        /// </summary>
        /// <param name="siloName">Silo to be restarted.</param>
        public async Task<SiloHandle> RestartStoppedSecondarySiloAsync(string siloName)
        {
            if (siloName == null) throw new ArgumentNullException(nameof(siloName));
            var siloHandle = this.Silos.Single(s => s.Name.Equals(siloName, StringComparison.Ordinal));
            var newInstance = await this.StartSiloAsync(this.Silos.IndexOf(siloHandle), this.options);
            lock (additionalSilos)
            {
                additionalSilos.Add(newInstance);
            }
            return newInstance;
        }

        /// <summary>
        /// Initialize the grain client. This should be already done by <see cref="Deploy()"/> or <see cref="DeployAsync"/>
        /// </summary>
        public async Task InitializeClientAsync()
        {
            WriteLog("Initializing Cluster Client");

            if (ClientHost is not null)
            {
                await StopClusterClientAsync();
            }

            var configurationBuilder = new ConfigurationBuilder();
            foreach (var source in ConfigurationSources)
            {
                configurationBuilder.Add(source);
            }
            var configuration = configurationBuilder.Build();

            this.ClientHost = TestClusterHostFactory.CreateClusterClient(
                "MainClient",
                configuration,
                hostBuilder =>
                {
                    hostBuilder.UseOrleansClient((context, clientBuilder) =>
                    {
                        Enum.TryParse<ConnectionTransportType>(context.Configuration[nameof(TestClusterOptions.ConnectionTransport)], out var transport);
                        switch (transport)
                        {
                            case ConnectionTransportType.TcpSocket:
                                break;
                            case ConnectionTransportType.InMemory:
                                clientBuilder.UseInMemoryConnectionTransport(_transportHub);
                                break;
                            case ConnectionTransportType.UnixSocket:
                                clientBuilder.UseUnixSocketConnection();
                                break;
                            default:
                                throw new ArgumentException($"Unsupported {nameof(ConnectionTransportType)}: {transport}");
                        }
                    });
                });
            await this.ClientHost.StartAsync();
        }

        /// <summary>
        /// Gets the configuration sources.
        /// </summary>
        /// <value>The configuration sources.</value>
        public IReadOnlyList<IConfigurationSource> ConfigurationSources { get; }

        private async Task InitializeAsync()
        {
            short silosToStart = this.options.InitialSilosCount;

            if (this.options.UseTestClusterMembership)
            {
                this.Primary = await StartSiloAsync(this.startedInstances, this.options);
                silosToStart--;
            }

            if (silosToStart > 0)
            {
                await this.StartAdditionalSilosAsync(silosToStart);
            }

            WriteLog("Done initializing cluster");

            if (this.options.InitializeClientOnDeploy)
            {
                await InitializeClientAsync();
            }
        }

        /// <summary>
        /// Default value for <see cref="CreateSiloAsync"/>, which creates a new silo handle.
        /// </summary>
        /// <param name="siloName">Name of the silo.</param>
        /// <param name="configuration">The configuration.</param>
        /// <returns>The silo handle.</returns>
        public async Task<SiloHandle> DefaultCreateSiloAsync(string siloName, IConfiguration configuration)
        {
            return await InProcessSiloHandle.CreateAsync(siloName, configuration, hostBuilder =>
            {
                hostBuilder.UseOrleans((context, siloBuilder) =>
                {
                    Enum.TryParse<ConnectionTransportType>(context.Configuration[nameof(TestClusterOptions.ConnectionTransport)], out var transport);
                    switch (transport)
                    {
                        case ConnectionTransportType.TcpSocket:
                            break;
                        case ConnectionTransportType.InMemory:
                            siloBuilder.UseInMemoryConnectionTransport(_transportHub);
                            break;
                        case ConnectionTransportType.UnixSocket:
                            siloBuilder.UseUnixSocketConnection();
                            break;
                        default:
                            throw new ArgumentException($"Unsupported {nameof(ConnectionTransportType)}: {transport}");
                    }
                });
            });
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
        public static async Task<SiloHandle> StartSiloAsync(TestCluster cluster, int instanceNumber, TestClusterOptions clusterOptions, IReadOnlyList<IConfigurationSource> configurationOverrides = null, bool startSiloOnNewPort = false)
        {
            if (cluster == null) throw new ArgumentNullException(nameof(cluster));
            return await cluster.StartSiloAsync(instanceNumber, clusterOptions, configurationOverrides, startSiloOnNewPort);
        }

        /// <summary>
        /// Starts a new silo.
        /// </summary>
        /// <param name="instanceNumber">The instance number to deploy</param>
        /// <param name="clusterOptions">The options to use.</param>
        /// <param name="configurationOverrides">Configuration overrides.</param>
        /// <param name="startSiloOnNewPort">Whether we start this silo on a new port, instead of the default one</param>
        /// <returns>A handle to the deployed silo.</returns>
        public async Task<SiloHandle> StartSiloAsync(int instanceNumber, TestClusterOptions clusterOptions, IReadOnlyList<IConfigurationSource> configurationOverrides = null, bool startSiloOnNewPort = false)
        {
            var configurationSources = this.ConfigurationSources.ToList();

            // Add overrides.
            if (configurationOverrides != null) configurationSources.AddRange(configurationOverrides);
            var siloSpecificOptions = TestSiloSpecificOptions.Create(this, clusterOptions, instanceNumber, startSiloOnNewPort);
            configurationSources.Add(new MemoryConfigurationSource
            {
                InitialData = siloSpecificOptions.ToDictionary()
            });

            var configurationBuilder = new ConfigurationBuilder();
            foreach (var source in configurationSources)
            {
                configurationBuilder.Add(source);
            }
            var configuration = configurationBuilder.Build();
            var handle = await this.CreateSiloAsync(siloSpecificOptions.SiloName, configuration);
            handle.InstanceNumber = (short)instanceNumber;
            Interlocked.Increment(ref this.startedInstances);
            return handle;
        }

        private async Task StopSiloAsync(SiloHandle instance, bool stopGracefully)
        {
            try
            {
                await instance.StopSiloAsync(stopGracefully).ConfigureAwait(false);
            }
            finally
            {
                await DisposeAsync(instance).ConfigureAwait(false);

                Interlocked.Decrement(ref this.startedInstances);
            }
        }

        /// <summary>
        /// Gets the log.
        /// </summary>
        /// <returns>The log contents.</returns>
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

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            await Task.Run(async () =>
            {
                foreach (var handle in this.SecondarySilos)
                {
                    await DisposeAsync(handle).ConfigureAwait(false);
                }

                if (this.Primary is not null)
                {
                    await DisposeAsync(Primary).ConfigureAwait(false);
                }

                await DisposeAsync(ClientHost).ConfigureAwait(false);
                ClientHost = null;

                this.PortAllocator?.Dispose();
            });

            _disposed = true;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            foreach (var handle in this.SecondarySilos)
            {
                handle.Dispose();
            }

            this.Primary?.Dispose();
            this.ClientHost?.Dispose();
            this.PortAllocator?.Dispose();

            _disposed = true;
        }

        private static async Task DisposeAsync(IDisposable value)
        {
            if (value is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (value is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
