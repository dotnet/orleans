using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.Hosting.Clustering;

namespace Orleans.Hosting.Kubernetes
{
    internal class KubernetesClusterProvider : IClusterProvider
    {
        private readonly k8s.Kubernetes _client;
        private readonly string _podLabelSelector;
        private readonly string _podNamespace;
        private readonly string _podName;

        public KubernetesClusterProvider(
            IOptionsMonitor<KubernetesHostingOptions> options,
            IOptions<ClusterOptions> clusterOptions)
        {
            var config = options.CurrentValue.GetClientConfiguration?.Invoke() ?? throw new ArgumentNullException(nameof(KubernetesHostingOptions) + "." + nameof(KubernetesHostingOptions.GetClientConfiguration));
            _client = new k8s.Kubernetes(config);
            _podLabelSelector = $"{KubernetesHostingOptions.ServiceIdLabel}={clusterOptions.Value.ServiceId},{KubernetesHostingOptions.ClusterIdLabel}={clusterOptions.Value.ClusterId}";
            _podNamespace = options.CurrentValue.Namespace;
            _podName = options.CurrentValue.PodName;
        }

        public async Task DeleteAsync(string name)
        {
            await _client.DeleteNamespacedPodAsync(name, _podNamespace);
        }

        public string Describe(string name)
        {
            return $"Pod, Name={name}, Namespace={_podNamespace}";
        }

        public async Task<IEnumerable<ExternalClusterMember>> ListMembersAsync(CancellationToken cancellation)
        {
            // Find the pods which correspond to this cluster
            var pods = await _client.ListNamespacedPodAsync(
                namespaceParameter: _podNamespace,
                labelSelector: _podLabelSelector,
                cancellationToken: cancellation);

            var clusterPods = new HashSet<string>
            {
                _podName
            };
            foreach (var pod in pods.Items)
            {
                clusterPods.Add(pod.Metadata.Name);
            }

            return clusterPods.Select(CreateMember);
        }

        public async IAsyncEnumerable<ClusterEvent> MonitorChangesAsync([EnumeratorCancellation] CancellationToken cancellation)
        {
            var pods = await _client.ListNamespacedPodWithHttpMessagesAsync(
                namespaceParameter: _podNamespace,
                labelSelector: _podLabelSelector,
                watch: true,
                cancellationToken: cancellation);

            await foreach (var (eventType, pod) in pods.WatchAsync<V1PodList, V1Pod>(cancellation))
            {
                if (cancellation.IsCancellationRequested)
                {
                    break;
                }

                if (eventType == WatchEventType.Modified)
                {
                    // TODO: Remember silo addresses for pods are restarting/terminating
                }

                if (eventType == WatchEventType.Deleted)
                {
                    var name = pod.Metadata.Name;

                    yield return new ClusterMemberDeleted(CreateMember(name));
                }
            }
        }

        private ExternalClusterMember CreateMember(string name)
        {
            var description = Describe(name);

            return new ExternalClusterMember(name, description)
            {
                IsCurrentSilo = name == _podName
            };
        }
    }
}
