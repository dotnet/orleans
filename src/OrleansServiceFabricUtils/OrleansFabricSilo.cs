using System;
using System.Diagnostics;
using System.Net;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Host;

namespace Microsoft.Orleans.ServiceFabric
{
    /// <summary>
    ///     Wrapper class for an Orleans silo running in the current host process.
    /// </summary>
    internal class OrleansFabricSilo
    {
        /// <summary>
        ///     The Azure table service connection string
        /// </summary>
        private readonly string connectionString;

        /// <summary>
        ///     The deployment id.
        /// </summary>
        private readonly string deploymentId;

        /// <summary>
        ///     The name of the silo.
        /// </summary>
        private readonly string siloName;

        /// <summary>
        ///     The task which completes when the silo stops running.
        /// </summary>
        private readonly TaskCompletionSource<int> stopped;

        /// <summary>
        ///     The host.
        /// </summary>
        private SiloHost host;

        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansFabricSilo"/> class.
        /// </summary>
        /// <param name="serviceName">
        /// The service name.
        /// </param>
        /// <param name="instanceId">
        /// The instance id.
        /// </param>
        /// <param name="siloEndpoint">
        /// The silo endpoint.
        /// </param>
        /// <param name="proxyEndpoint">
        /// The proxy endpoint.
        /// </param>
        /// <param name="connectionString">
        /// The Azure table service connection string.
        /// </param>
        public OrleansFabricSilo(
            Uri serviceName, 
            long instanceId, 
            IPEndPoint siloEndpoint, 
            IPEndPoint proxyEndpoint, 
            string connectionString)
        {
            this.stopped = new TaskCompletionSource<int>();
            this.SiloEndpoint = siloEndpoint;
            this.ProxyEndpoint = proxyEndpoint;
            this.connectionString = connectionString;
            this.deploymentId = OrleansFabricUtility.GetDeploymentId(serviceName);
            this.siloName = this.deploymentId + "_" + instanceId.ToString("X");
        }

        /// <summary>
        ///     Gets the silo endpoint.
        /// </summary>
        public IPEndPoint SiloEndpoint { get; }

        /// <summary>
        ///     Gets the proxy endpoint.
        /// </summary>
        public IPEndPoint ProxyEndpoint { get; }

        /// <summary>
        ///     Gets the task which completes when the silo stops running.
        /// </summary>
        public Task Stopped => this.stopped.Task;

        /// <summary>
        /// Gets the unique address of this silo.
        /// </summary>
        public SiloAddress Address => SiloAddress.New(this.host.NodeConfig.Endpoint, this.host.NodeConfig.Generation);

        /// <summary>
        /// Starts the silo.
        /// </summary>
        /// <param name="config">
        /// The config.
        /// </param>
        /// <returns>
        /// Whether or not initialization was successful.
        /// </returns>
        /// <exception cref="OrleansException">
        /// An exception occurred starting the silo.
        /// </exception>
        public bool Start(ClusterConfiguration config)
        {
            try
            {
                Trace.TraceInformation(
                    $"Starting silo. Name: {this.siloName}, DeploymentId: {this.deploymentId}, Primary Endpoint: {this.SiloEndpoint}");

                // Configure this Orleans silo instance
                if (config == null)
                {
                    Trace.TraceInformation("Loading configuration from default locations.");
                    this.host = new SiloHost(this.siloName);
                    this.host.LoadOrleansConfig();
                }
                else
                {
                    Trace.TraceInformation("Using provided configuration.");
                    this.host = new SiloHost(this.siloName, config);
                }

                // Configure the silo for the current environment.
                var generation = SiloAddress.AllocateNewGeneration();
                this.host.SetSiloType(Silo.SiloType.Secondary);
                
                this.host.SetDeploymentId(this.deploymentId, this.connectionString);
                this.host.SetSiloEndpoint(this.SiloEndpoint, generation);
                this.host.SetProxyEndpoint(this.ProxyEndpoint);

                this.host.InitializeOrleansSilo();
                Trace.TraceInformation($"Successfully initialized Orleans silo '{this.siloName}'.");
                Trace.TraceInformation($"Starting Orleans silo '{this.siloName}'.");

                var ok = this.host.StartOrleansSilo();
                if (ok)
                {
                    Trace.TraceInformation(
                        $"Successfully started Orleans silo '{this.siloName}'.");
                }
                else
                {
                    Trace.TraceInformation($"Failed to start Orleans silo '{this.siloName}'");
                }

                this.MonitorSilo();
                return ok;
            }
            catch (Exception e)
            {
                this.stopped.TrySetException(e);
                this.Abort();

                throw;
            }
        }

        /// <summary>
        ///     Stop this Orleans silo executing.
        /// </summary>
        public void Stop()
        {
            Trace.TraceInformation($"Stopping Orleans silo '{this.siloName}'.");
            var silo = this.host;
            if (silo != null)
            {
                try
                {
                    this.host.StopOrleansSilo();
                    this.host.UnInitializeOrleansSilo();
                }
                catch (Exception exception)
                {
                    Trace.TraceWarning($"Exception stopping Orleans silo: {exception}.");
                }

                this.stopped.TrySetResult(0);
            }

            Trace.TraceInformation($"Orleans silo '{this.siloName}' shutdown.");
        }

        /// <summary>
        ///     The abort.
        /// </summary>
        public void Abort()
        {
            this.host?.StopOrleansSilo();
            this.host?.UnInitializeOrleansSilo();
            this.host?.Dispose();
            this.host = null;
        }

        /// <summary>
        ///     Monitors the silo.
        /// </summary>
        private void MonitorSilo()
        {
            Action monitor = () =>
            {
                try
                {
                    if (this.host != null && this.host.IsStarted)
                    {
                        Trace.TraceInformation($"Monitoring Orleans silo '{this.siloName}' for shutdown event.");
                        this.host?.WaitForOrleansSiloShutdown();
                        this.stopped.TrySetResult(0);
                    }
                    else
                    {
                        this.stopped.TrySetException(new ApplicationException($"Orleans silo '{this.siloName}' failed to start correctly - aborting"));
                    }
                }
                catch (Exception e)
                {
                    this.stopped.TrySetException(e);
                }
            };
            Task.Factory.StartNew(monitor, TaskCreationOptions.LongRunning);
        }
    }
}