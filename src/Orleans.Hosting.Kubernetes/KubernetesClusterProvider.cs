using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.Hosting.Clustering;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Hosting.Kubernetes
{
    internal sealed partial class KubernetesClusterProvider : IClusterProvider
    {
        private const string ExampleRoleBinding =
            """
            kind: Role
            apiVersion: rbac.authorization.k8s.io/v1
            metadata:
              name: pod-updater
            rules:
            - apiGroups: [ "" ]
              resources: ["pods"]
              verbs: ["get", "watch", "list", "patch", "delete"]
            ---
            kind: RoleBinding
            apiVersion: rbac.authorization.k8s.io/v1
            metadata:
              name: pod-updater-binding
            subjects:
            - kind: ServiceAccount
              name: default
              apiGroup: ''
            roleRef:
              kind: Role
              name: pod-updater
              apiGroup: ''
            """;

        private readonly ClusterOptions _clusterOptions;
        private readonly k8s.Kubernetes _client;
        private readonly ILogger<KubernetesClusterProvider> _logger;
        private readonly string _podLabelSelector;
        private readonly string _podNamespace;
        private readonly string _podName;

        public KubernetesClusterProvider(
            ILogger<KubernetesClusterProvider> logger,
            IOptionsMonitor<KubernetesHostingOptions> options,
            IOptions<ClusterOptions> clusterOptions)
        {
            _logger = logger;
            _clusterOptions = clusterOptions.Value;
            _client = new k8s.Kubernetes(options.CurrentValue.ClientConfiguration);
            _podLabelSelector = $"{KubernetesHostingOptions.ServiceIdLabel}={_clusterOptions.ServiceId},{KubernetesHostingOptions.ClusterIdLabel}={_clusterOptions.ClusterId}";
            _podNamespace = options.CurrentValue.Namespace!;
            _podName = options.CurrentValue.PodName!;
        }

        public async Task DeleteAsync(string name, CancellationToken cancellation)
        {
            try
            {
                await _client.DeleteNamespacedPodAsync(name, _podNamespace, cancellationToken: cancellation);
            }
            catch (HttpOperationException exception) when (exception.Response.StatusCode is HttpStatusCode.NotFound)
            {
            }
        }

        public string Describe(string name) => $"Pod, Name={name}, Namespace={_podNamespace}";

        public async Task<IEnumerable<ExternalClusterMember>> ListMembersAsync(CancellationToken cancellation)
        {
            try
            {
                await AddClusterOptionsToPodLabels(cancellation);

                var pods = await _client.ListNamespacedPodAsync(
                    namespaceParameter: _podNamespace,
                    labelSelector: _podLabelSelector,
                    cancellationToken: cancellation);

                var clusterPods = new HashSet<string>(StringComparer.Ordinal)
                {
                    _podName
                };

                foreach (var pod in pods.Items)
                {
                    clusterPods.Add(pod.Metadata.Name);
                }

                return clusterPods.Select(CreateMember);
            }
            catch (HttpOperationException exception) when (exception.Response.StatusCode is HttpStatusCode.Forbidden)
            {
                LogErrorInsufficientPermissions(exception);
                throw;
            }
        }

        public async IAsyncEnumerable<ClusterEvent> MonitorChangesAsync([EnumeratorCancellation] CancellationToken cancellation)
        {
            var pods = _client.CoreV1.WatchListNamespacedPodAsync(
                namespaceParameter: _podNamespace,
                labelSelector: _podLabelSelector,
                cancellationToken: cancellation);

            await foreach (var (eventType, pod) in pods.WithCancellation(cancellation))
            {
                if (eventType == WatchEventType.Deleted)
                {
                    yield return new ClusterMemberDeleted(CreateMember(pod.Metadata.Name));
                }
            }
        }

        private async Task AddClusterOptionsToPodLabels(CancellationToken cancellation)
        {
            var thisPod = await _client.ReadNamespacedPodAsync(_podName, namespaceParameter: _podNamespace, cancellationToken: cancellation);
            var labels = thisPod.Labels();
            if (labels is null
                || !labels.TryGetValue(KubernetesHostingOptions.ServiceIdLabel, out var serviceId) || !string.Equals(_clusterOptions.ServiceId, serviceId, StringComparison.Ordinal)
                || !labels.TryGetValue(KubernetesHostingOptions.ClusterIdLabel, out var clusterId) || !string.Equals(_clusterOptions.ClusterId, clusterId, StringComparison.Ordinal))
            {
                var patch =
                    $$"""
                    {
                        "metadata": {
                            "labels": {
                                "{{KubernetesHostingOptions.ClusterIdLabel}}": "{{_clusterOptions.ClusterId}}",
                                "{{KubernetesHostingOptions.ServiceIdLabel}}": "{{_clusterOptions.ServiceId}}"
                            }
                        }
                    }
                    """;
                await _client.PatchNamespacedPodAsync(new V1Patch(patch, V1Patch.PatchType.MergePatch), _podName, _podNamespace, cancellationToken: cancellation);
            }
        }

        private ExternalClusterMember CreateMember(string name) =>
            new(name, Describe(name))
            {
                IsCurrentSilo = string.Equals(name, _podName, StringComparison.Ordinal)
            };

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = $"Unable to monitor pods due to insufficient permissions. Ensure that this pod has an appropriate Kubernetes role binding. Here is an example role binding:\n{ExampleRoleBinding}"
        )]
        private partial void LogErrorInsufficientPermissions(Exception exception);
    }
}
