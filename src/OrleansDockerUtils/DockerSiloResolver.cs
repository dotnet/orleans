using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Orleans.Docker.Models;
using Microsoft.Orleans.Docker.Utilities;
using Orleans;
using Orleans.Runtime;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Orleans.Docker
{
    internal class DockerSiloResolver : IDockerSiloResolver
    {
        public DateTime LastRefreshTime { get; private set; }
        public TimeSpan RefreshPeriod { get; } = TimeSpan.FromSeconds(30);
        private readonly ConcurrentDictionary<IDockerStatusListener, IDockerStatusListener> subscribers =
            new ConcurrentDictionary<IDockerStatusListener, IDockerStatusListener>();
        private readonly string deploymentId;
        private readonly Logger log;
        private readonly object updateLock = new object();
        private readonly DockerClient dockerClient;
        private List<DockerSiloInfo> silos;
        private const string LABEL_FILTER = "label";
        private const string RUNNING_STATE = "running";
        private Timer timer;

        public DockerSiloResolver(
            string deploymentId,
            DockerClient dockerClient,
            Func<string, Logger> loggerFactory = null)
        {
            this.deploymentId = deploymentId;
            this.dockerClient = dockerClient;
            log = loggerFactory?.Invoke(nameof(DockerSiloResolver));
        }

        public async Task Refresh()
        {
            try
            {
                IList<ContainerListResponse> containerList = await dockerClient.Containers.ListContainersAsync(
                       new ContainersListParameters
                       {
                           Filters = new Dictionary<string, IDictionary<string, bool>>
                                   {
                                        {LABEL_FILTER,
                                            new Dictionary<string, bool>
                                                {
                                                    {$"{DockerLabels.DEPLOYMENT_ID}={deploymentId}", true},
                                                    {$"{DockerLabels.IS_DOCKER_SILO}", true}
                                                }
                                        },
                                   }
                       });

                ContainerInspectResponse[] inspectionResults = 
                    await Task.WhenAll(
                        containerList.Where(c=>c.State.Equals(RUNNING_STATE, StringComparison.OrdinalIgnoreCase))
                        .Select(c => dockerClient.Containers.InspectContainerAsync(c.ID)));

                lock (updateLock)
                {
                    silos = inspectionResults
                        .Select(GetSiloFromContainer).ToList();
                }

                LastRefreshTime = DateTime.UtcNow;
                NotifySubscribers();
            }
            catch (Exception exception)
            {
                log?.Warn(
                    (int)ErrorCode.Docker_MembershipOracle_ExceptionRefreshingSilos,
                    "Exception refreshing silos.",
                    exception);
                throw;
            }
        }

        private static DockerSiloInfo GetSiloFromContainer(ContainerInspectResponse container)
        {
            var network = container.NetworkSettings.Networks.First().Value;
            var hostname = container.Config.Hostname;
            var siloIPAddress = IPAddress.Parse(network.IPAddress);
            var siloPort = container.Config.Labels[DockerLabels.SILO_PORT];
            var siloEndpoint = new IPEndPoint(siloIPAddress, int.Parse(siloPort));

            IPEndPoint gatewayEndpoint = null;
            string gatewayPort;
            if (container.Config.Labels.TryGetValue(DockerLabels.GATEWAY_PORT, out gatewayPort))
                gatewayEndpoint = new IPEndPoint(siloIPAddress, int.Parse(gatewayPort));

            var generation = int.Parse(container.Config.Labels[DockerLabels.GENERATION]);

            return new DockerSiloInfo(hostname, siloEndpoint, gatewayEndpoint, generation);
        }

        public void Subscribe(IDockerStatusListener handler)
        {
            if (subscribers.TryAdd(handler, handler))
                handler.OnUpdate(silos.ToArray());

            if (timer == null)
            {
                timer = new Timer(
                self => ((DockerSiloResolver)self).Refresh().Ignore(),
                this,
                RefreshPeriod,
                RefreshPeriod);

                Refresh().Ignore();
            }
        }

        public void Unsubscribe(IDockerStatusListener handler)
        {
            subscribers.TryRemove(handler, out handler);

            if (subscribers.Count == 0)
            {
                timer?.Dispose();
                timer = null;
            }
        }

        /// <summary>
        /// Notifies subscribers of updates.
        /// </summary>
        private void NotifySubscribers()
        {
            var copy = silos.ToArray();
            foreach (var observer in subscribers.Values)
            {
                observer.OnUpdate(copy);
            }
        }
    }
}
