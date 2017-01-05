using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Orleans.Docker.Models;
using Orleans.Runtime;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace Microsoft.Orleans.Docker
{
    internal class DockerSiloResolver : IDockerSiloResolver
    {
        private readonly ConcurrentDictionary<IDockerStatusListener, IDockerStatusListener> subscribers =
            new ConcurrentDictionary<IDockerStatusListener, IDockerStatusListener>();
        private readonly string deploymentId;
        private readonly Logger log;
        private readonly object updateLock = new object();
        private readonly DockerClient dockerClient;
        private List<DockerSiloInfo> silos;
        private const string LABEL_FILTER = "label";

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
            var result = await Task.WhenAll((await dockerClient.Containers.ListContainersAsync(
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
                })).Select(c => dockerClient.Containers.InspectContainerAsync(c.ID)));

            lock (updateLock)
            {
                silos = result.Select(_ => 
                {
                    return new DockerSiloInfo(_.Config.Hostname,
                        new IPEndPoint(IPAddress.Parse(_.NetworkSettings.Networks.First().Value.IPAddress), 
                            int.Parse(_.Config.Labels[DockerLabels.SILO_PORT])),
                        new IPEndPoint(IPAddress.Parse(_.NetworkSettings.Networks.First().Value.IPAddress), 
                            int.Parse(_.Config.Labels[DockerLabels.GATEWAY_PORT])),
                        int.Parse(_.Config.Labels[DockerLabels.GENERATION]));
                }).ToList(); 
            }

            NotifySubscribers();
        }

        public void Subscribe(IDockerStatusListener handler)
        {
            subscribers.TryAdd(handler, handler);
        }

        public void Unsubscribe(IDockerStatusListener handler)
        {
            subscribers.TryRemove(handler, out handler);
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
