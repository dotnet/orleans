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
using Orleans.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using Orleans.TestingHost.InMemoryTransport;
using System.Net;
using Orleans.Statistics;
using Orleans.TestingHost.InProcess;
using Orleans.Runtime.Hosting;
using Orleans.GrainDirectory;
using Orleans.Messaging;
using Orleans.Hosting;
using Orleans.Runtime.TestHooks;
using Orleans.Configuration.Internal;
using Orleans.TestingHost.Logging;

namespace Orleans.TestingHost;

/// <summary>
/// A host class for local testing with Orleans using in-process silos. 
/// </summary>
public sealed class InProcessTestCluster : IDisposable, IAsyncDisposable
{
    private readonly List<InProcessSiloHandle> _silos = [];
    private readonly StringBuilder _log = new();
    private readonly InMemoryTransportConnectionHub _transportHub = new();
    private readonly InProcessGrainDirectory _grainDirectory;
    private readonly InProcessMembershipTable _membershipTable;
    private bool _disposed;
    private int _startedInstances;

    /// <summary>
    /// Collection of all known silos.
    /// </summary>
    public ReadOnlyCollection<InProcessSiloHandle> Silos
    {
        get
        {
            lock (_silos)
            {
                return new List<InProcessSiloHandle>(_silos).AsReadOnly();
            }
        }
    }

    /// <summary>
    /// Options used to configure the test cluster.
    /// </summary>
    /// <remarks>This is the options you configured your test cluster with, or the default one. 
    /// If the cluster is being configured via ClusterConfiguration, then this object may not reflect the true settings.
    /// </remarks>
    public InProcessTestClusterOptions Options { get; }

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
    public IClusterClient Client => ClientHost?.Services.GetRequiredService<IInternalClusterClient>();

    /// <summary>
    /// The port allocator.
    /// </summary>
    public ITestClusterPortAllocator PortAllocator { get; }

    /// <summary>
    /// Configures the test cluster plus client in-process.
    /// </summary>
    public InProcessTestCluster(
        InProcessTestClusterOptions options,
        ITestClusterPortAllocator portAllocator)
    {
        Options = options;
        PortAllocator = portAllocator;
        _membershipTable = new(options.ClusterId);
        _grainDirectory = new(_membershipTable.GetSiloStatus);
    }

    /// <summary>
    /// Returns the <see cref="IServiceProvider"/> associated with the given <paramref name="silo"/>.
    /// </summary>
    /// <param name="silo">The silo process to the the service provider for.</param>
    /// <remarks>If <paramref name="silo"/> is <see langword="null"/> one of the existing silos will be picked randomly.</remarks>
    public IServiceProvider GetSiloServiceProvider(SiloAddress silo = null)
    {
        if (silo != null)
        {
            var handle = Silos.FirstOrDefault(x => x.SiloAddress.Equals(silo));
            return handle != null ? handle.SiloHost.Services :
                throw new ArgumentException($"The provided silo address '{silo}' is unknown.");
        }
        else
        {
            var index = Random.Shared.Next(Silos.Count);
            return Silos[index].SiloHost.Services;
        }
    }

    /// <summary>
    /// Deploys the cluster using the specified configuration and starts the client in-process.
    /// </summary>
    public async Task DeployAsync()
    {
        if (_silos.Count > 0) throw new InvalidOperationException("Cluster host already deployed.");

        AppDomain.CurrentDomain.UnhandledException += ReportUnobservedException;

        try
        {
            string startMsg = "----------------------------- STARTING NEW UNIT TEST SILO HOST: " + GetType().FullName + " -------------------------------------";
            WriteLog(startMsg);
            await InitializeAsync();

            if (Options.InitializeClientOnDeploy)
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
            var silos = Silos;
            var expectedCount = silos.Count;
            var remainingSilos = expectedCount;

            foreach (var silo in silos)
            {
                var hooks = InternalClient.GetTestHooks(silo);
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
    public IEnumerable<InProcessSiloHandle> GetActiveSilos()
    {
        var additional = new List<InProcessSiloHandle>();
        lock (_silos)
        {
            additional.AddRange(_silos);
        }

        WriteLog("GetActiveSilos: {0} Silos={1}",
            additional.Count, Runtime.Utils.EnumerableToString(additional));

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
    public InProcessSiloHandle GetSiloForAddress(SiloAddress siloAddress)
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
        var clusterMembershipOptions = Client.ServiceProvider.GetRequiredService<IOptions<ClusterMembershipOptions>>().Value;
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
    public InProcessSiloHandle StartAdditionalSilo()
    {
        return StartAdditionalSiloAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Start an additional silo, so that it joins the existing cluster.
    /// </summary>
    /// <returns>SiloHandle for the newly started silo.</returns>
    public async Task<InProcessSiloHandle> StartAdditionalSiloAsync()
    {
        return (await StartSilosAsync(1)).Single();
    }

    /// <summary>
    /// Start an additional silo, so that it joins the existing cluster.
    /// </summary>
    /// <returns>SiloHandle for the newly started silo.</returns>
    [Obsolete("Use overload which does not have a 'startAdditionalSiloOnNewPort' parameter.")]
    public InProcessSiloHandle StartAdditionalSilo(bool startAdditionalSiloOnNewPort)
    {
        return StartAdditionalSiloAsync(startAdditionalSiloOnNewPort).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Start an additional silo, so that it joins the existing cluster.
    /// </summary>
    /// <returns>SiloHandle for the newly started silo.</returns>
    public async Task<InProcessSiloHandle> StartAdditionalSiloAsync(bool startAdditionalSiloOnNewPort)
    {
        return (await StartSilosAsync(1)).Single();
    }

    /// <summary>
    /// Start a number of additional silo, so that they join the existing cluster.
    /// </summary>
    /// <param name="silosToStart">Number of silos to start.</param>
    /// <param name="startAdditionalSiloOnNewPort"></param>
    /// <returns>List of SiloHandles for the newly started silos.</returns>
    [Obsolete("Use overload which does not have a 'startAdditionalSiloOnNewPort' parameter.")]
    public async Task<List<InProcessSiloHandle>> StartSilosAsync(int silosToStart, bool startAdditionalSiloOnNewPort)
    {
        return await StartSilosAsync(silosToStart);
    }

    /// <summary>
    /// Start a number of additional silo, so that they join the existing cluster.
    /// </summary>
    /// <param name="silosToStart">Number of silos to start.</param>
    /// <returns>List of SiloHandles for the newly started silos.</returns>
    public async Task<List<InProcessSiloHandle>> StartSilosAsync(int silosToStart)
    {
        var instances = new List<InProcessSiloHandle>();
        if (silosToStart > 0)
        {
            var siloStartTasks = Enumerable.Range(_startedInstances, silosToStart)
                .Select(instanceNumber => Task.Run(() => StartSiloAsync((short)instanceNumber, Options))).ToArray();

            try
            {
                await Task.WhenAll(siloStartTasks);
            }
            catch (Exception)
            {
                lock (_silos)
                {
                    _silos.AddRange(siloStartTasks.Where(t => t.Exception == null).Select(t => t.Result));
                }

                throw;
            }

            instances.AddRange(siloStartTasks.Select(t => t.Result));
            lock (_silos)
            {
                _silos.AddRange(instances);
            }
        }

        return instances;
    }

    /// <summary>
    /// Stop all silos.
    /// </summary>
    public async Task StopSilosAsync()
    {
        foreach (var instance in _silos.ToList())
        {
            await StopSiloAsync(instance);
        }
    }

    /// <summary>
    /// Stop cluster client as an asynchronous operation.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task StopClusterClientAsync()
    {
        var client = ClientHost;
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
        await StopSilosAsync();
        AppDomain.CurrentDomain.UnhandledException -= ReportUnobservedException;
    }

    /// <summary>
    /// Do a semi-graceful Stop of the specified silo.
    /// </summary>
    /// <param name="instance">Silo to be stopped.</param>
    public async Task StopSiloAsync(InProcessSiloHandle instance)
    {
        if (instance != null)
        {
            await StopSiloAsync(instance, true);
            lock (_silos)
            {
                _silos.Remove(instance);
            }
        }
    }

    /// <summary>
    /// Do an immediate Kill of the specified silo.
    /// </summary>
    /// <param name="instance">Silo to be killed.</param>
    public async Task KillSiloAsync(InProcessSiloHandle instance)
    {
        if (instance != null)
        {
            // do NOT stop, just kill directly, to simulate crash.
            await StopSiloAsync(instance, false);
            lock (_silos)
            {
                _silos.Remove(instance);
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
    public async Task<InProcessSiloHandle> RestartSiloAsync(InProcessSiloHandle instance)
    {
        if (instance != null)
        {
            var instanceNumber = instance.InstanceNumber;
            await StopSiloAsync(instance);
            var newInstance = await StartSiloAsync(instanceNumber, Options);
            lock (_silos)
            {
                _silos.Add(newInstance);
            }

            return newInstance;
        }

        return null;
    }

    /// <summary>
    /// Restart a previously stopped.
    /// </summary>
    /// <param name="siloName">Silo to be restarted.</param>
    public async Task<InProcessSiloHandle> RestartStoppedSecondarySiloAsync(string siloName)
    {
        if (siloName == null) throw new ArgumentNullException(nameof(siloName));
        var siloHandle = Silos.Single(s => s.Name.Equals(siloName, StringComparison.Ordinal));
        var newInstance = await StartSiloAsync(Silos.IndexOf(siloHandle), Options);
        lock (_silos)
        {
            _silos.Add(newInstance);
        }
        return newInstance;
    }

    /// <summary>
    /// Initialize the grain client. This should be already done by <see cref="DeployAsync"/>
    /// </summary>
    public async Task InitializeClientAsync()
    {
        WriteLog("Initializing Cluster Client");

        if (ClientHost is not null)
        {
            await StopClusterClientAsync();
        }

        var hostBuilder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development,
            ApplicationName = "TestClusterClient",
            DisableDefaults = true,
        });

        foreach (var hostDelegate in Options.ClientHostConfigurationDelegates)
        {
            hostDelegate(hostBuilder);
        }

        hostBuilder.UseOrleansClient(clientBuilder =>
        {
            clientBuilder.Configure<ClusterOptions>(o =>
            {
                o.ClusterId = Options.ClusterId;
                o.ServiceId = Options.ServiceId;
            });

            if (Options.UseTestClusterMembership)
            {
                clientBuilder.Services.AddSingleton<IGatewayListProvider>(_membershipTable);
            }

            clientBuilder.UseInMemoryConnectionTransport(_transportHub);
        });

        TryConfigureFileLogging(Options, hostBuilder.Services, "TestClusterClient");

        ClientHost = hostBuilder.Build();
        await ClientHost.StartAsync();
    }

    private async Task InitializeAsync()
    {
        var silosToStart = Options.InitialSilosCount;

        if (silosToStart > 0)
        {
            await StartSilosAsync(silosToStart);
        }

        WriteLog("Done initializing cluster");

        if (Options.InitializeClientOnDeploy)
        {
            await InitializeClientAsync();
        }
    }

    public async Task<InProcessSiloHandle> CreateSiloAsync(InProcessTestSiloSpecificOptions siloOptions)
    {
        var host = await Task.Run(async () =>
        {
            var siloName = siloOptions.SiloName;

            var appBuilder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                ApplicationName = siloName,
                EnvironmentName = Environments.Development,
                DisableDefaults = true
            });

            var services = appBuilder.Services;
            TryConfigureFileLogging(Options, services, siloName);

            if (Debugger.IsAttached)
            {
                // Test is running inside debugger - Make timeout ~= infinite
                services.Configure<SiloMessagingOptions>(op => op.ResponseTimeout = TimeSpan.FromMilliseconds(1000000));
            }

            foreach (var hostDelegate in Options.SiloHostConfigurationDelegates)
            {
                hostDelegate(siloOptions, appBuilder);
            }

            appBuilder.UseOrleans(siloBuilder =>
            {
                siloBuilder.Configure<ClusterOptions>(o =>
                {
                    o.ClusterId = Options.ClusterId;
                    o.ServiceId = Options.ServiceId;
                });

                siloBuilder.Configure<SiloOptions>(o =>
                {
                    o.SiloName = siloOptions.SiloName;
                });

                siloBuilder.Configure<EndpointOptions>(o =>
                {
                    o.AdvertisedIPAddress = IPAddress.Loopback;
                    o.SiloPort = siloOptions.SiloPort;
                    o.GatewayPort = siloOptions.GatewayPort;
                });

                siloBuilder.Services
                    .Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(30));

                if (Options.UseTestClusterMembership)
                {
                    services.AddSingleton<IMembershipTable>(_membershipTable);
                    siloBuilder.AddGrainDirectory(GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY, (_, _) => _grainDirectory);
                }

                siloBuilder.UseInMemoryConnectionTransport(_transportHub);

                services.AddSingleton<TestHooksEnvironmentStatisticsProvider>();
                services.AddSingleton<TestHooksSystemTarget>();
                if (!Options.UseRealEnvironmentStatistics)
                {
                    services.AddFromExisting<IEnvironmentStatisticsProvider, TestHooksEnvironmentStatisticsProvider>();
                }
            });

            var host = appBuilder.Build();
            InitializeTestHooksSystemTarget(host);
            await host.StartAsync();
            return host;
        });

        return new InProcessSiloHandle
        {
            Name = siloOptions.SiloName,
            SiloHost = host,
            SiloAddress = host.Services.GetRequiredService<ILocalSiloDetails>().SiloAddress,
            GatewayAddress = host.Services.GetRequiredService<ILocalSiloDetails>().GatewayAddress,
        };
    }

    /// <summary>
    /// Start a new silo in the target cluster
    /// </summary>
    /// <param name="cluster">The InProcessTestCluster in which the silo should be deployed</param>
    /// <param name="instanceNumber">The instance number to deploy</param>
    /// <param name="clusterOptions">The options to use.</param>
    /// <returns>A handle to the silo deployed</returns>
    public static async Task<InProcessSiloHandle> StartSiloAsync(InProcessTestCluster cluster, int instanceNumber, InProcessTestClusterOptions clusterOptions)
    {
        if (cluster == null) throw new ArgumentNullException(nameof(cluster));
        return await cluster.StartSiloAsync(instanceNumber, clusterOptions);
    }

    /// <summary>
    /// Start a new silo in the target cluster
    /// </summary>
    /// <param name="cluster">The InProcessTestCluster in which the silo should be deployed</param>
    /// <param name="instanceNumber">The instance number to deploy</param>
    /// <param name="clusterOptions">The options to use.</param>
    /// <param name="configurationOverrides">Configuration overrides.</param>
    /// <param name="startSiloOnNewPort">Whether we start this silo on a new port, instead of the default one</param>
    /// <returns>A handle to the silo deployed</returns>
    [Obsolete("Use the overload which does not have a 'startSiloOnNewPort' parameter.")]
    public static async Task<InProcessSiloHandle> StartSiloAsync(InProcessTestCluster cluster, int instanceNumber, InProcessTestClusterOptions clusterOptions, IReadOnlyList<IConfigurationSource> configurationOverrides, bool startSiloOnNewPort)
    {
        if (cluster == null) throw new ArgumentNullException(nameof(cluster));
        return await cluster.StartSiloAsync(instanceNumber, clusterOptions, configurationOverrides, startSiloOnNewPort);
    }

    /// <summary>
    /// Starts a new silo.
    /// </summary>
    /// <param name="instanceNumber">The instance number to deploy</param>
    /// <param name="clusterOptions">The options to use.</param>
    /// <returns>A handle to the deployed silo.</returns>
    public async Task<InProcessSiloHandle> StartSiloAsync(int instanceNumber, InProcessTestClusterOptions clusterOptions)
    {
        var siloOptions = InProcessTestSiloSpecificOptions.Create(this, clusterOptions, instanceNumber, assignNewPort: true);
        var handle = await CreateSiloAsync(siloOptions);
        handle.InstanceNumber = (short)instanceNumber;
        Interlocked.Increment(ref _startedInstances);
        return handle;
    }

    /// <summary>
    /// Starts a new silo.
    /// </summary>
    /// <param name="instanceNumber">The instance number to deploy</param>
    /// <param name="clusterOptions">The options to use.</param>
    /// <param name="configurationOverrides">Configuration overrides.</param>
    /// <param name="startSiloOnNewPort">Whether we start this silo on a new port, instead of the default one</param>
    /// <returns>A handle to the deployed silo.</returns>
    [Obsolete("Use the overload which does not have a 'startSiloOnNewPort' parameter.")]
    public async Task<InProcessSiloHandle> StartSiloAsync(int instanceNumber, InProcessTestClusterOptions clusterOptions, IReadOnlyList<IConfigurationSource> configurationOverrides, bool startSiloOnNewPort)
    {
        return await StartSiloAsync(instanceNumber, clusterOptions);
    }

    private async Task StopSiloAsync(InProcessSiloHandle instance, bool stopGracefully)
    {
        try
        {
            await instance.StopSiloAsync(stopGracefully).ConfigureAwait(false);
        }
        finally
        {
            await DisposeAsync(instance).ConfigureAwait(false);

            Interlocked.Decrement(ref _startedInstances);
        }
    }

    /// <summary>
    /// Gets the log.
    /// </summary>
    /// <returns>The log contents.</returns>
    public string GetLog()
    {
        return _log.ToString();
    }

    private void ReportUnobservedException(object sender, UnhandledExceptionEventArgs eventArgs)
    {
        Exception exception = (Exception)eventArgs.ExceptionObject;
        WriteLog("Unobserved exception: {0}", exception);
    }

    private void WriteLog(string format, params object[] args)
    {
        _log.AppendFormat(format + Environment.NewLine, args);
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
            foreach (var handle in Silos)
            {
                await DisposeAsync(handle).ConfigureAwait(false);
            }

            await DisposeAsync(ClientHost).ConfigureAwait(false);
            ClientHost = null;

            PortAllocator?.Dispose();
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

        foreach (var handle in Silos)
        {
            handle.Dispose();
        }

        ClientHost?.Dispose();
        PortAllocator?.Dispose();

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
    private static void TryConfigureFileLogging(InProcessTestClusterOptions options, IServiceCollection services, string name)
    {
        if (options.ConfigureFileLogging)
        {
            var fileName = TestingUtils.CreateTraceFileName(name, options.ClusterId);
            services.AddLogging(loggingBuilder => loggingBuilder.AddFile(fileName));
        }
    }

    private static void InitializeTestHooksSystemTarget(IHost host)
    {
        _ = host.Services.GetRequiredService<TestHooksSystemTarget>();
    }
}
