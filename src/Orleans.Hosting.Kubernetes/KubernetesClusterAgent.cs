using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Hosting.Kubernetes
{
    /// <summary>
    /// Reflects cluster configuration changes between Orleans and Kubernetes.
    /// </summary>
    public sealed class KubernetesClusterAgent : ILifecycleParticipant<ISiloLifecycle>
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
              verbs: ["get", "watch", "list", "patch"]
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

        private readonly IOptionsMonitor<KubernetesHostingOptions> _options;
        private readonly ClusterOptions _clusterOptions;
        private readonly IClusterMembershipService _clusterMembershipService;
        private readonly KubernetesClientConfiguration _config;
        private readonly k8s.Kubernetes _client;
        private readonly string _podLabelSelector;
        private readonly string _podNamespace;
        private readonly string _podName;
        private readonly ILocalSiloDetails _localSiloDetails;
        private readonly ILogger<KubernetesClusterAgent> _logger;
        private readonly CancellationTokenSource _shutdownToken;
        private readonly SemaphoreSlim _pauseMonitoringSemaphore = new SemaphoreSlim(0);
        private volatile bool _enableMonitoring;
        private Task _runTask;

        public KubernetesClusterAgent(
            IClusterMembershipService clusterMembershipService,
            ILogger<KubernetesClusterAgent> logger,
            IOptionsMonitor<KubernetesHostingOptions> options,
            IOptions<ClusterOptions> clusterOptions,
            ILocalSiloDetails localSiloDetails)
        {
            _localSiloDetails = localSiloDetails;
            _logger = logger;
            _shutdownToken = new CancellationTokenSource();
            _options = options;
            _clusterOptions = clusterOptions.Value;
            _clusterMembershipService = clusterMembershipService;
            _config = _options.CurrentValue.GetClientConfiguration?.Invoke() ?? throw new ArgumentNullException(nameof(KubernetesHostingOptions) + "." + nameof(KubernetesHostingOptions.GetClientConfiguration));
            _client = new k8s.Kubernetes(_config);
            _podLabelSelector = $"{KubernetesHostingOptions.ServiceIdLabel}={_clusterOptions.ServiceId},{KubernetesHostingOptions.ClusterIdLabel}={_clusterOptions.ClusterId}";
            _podNamespace = _options.CurrentValue.Namespace;
            _podName = _options.CurrentValue.PodName;
        }

        public void Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe(
                nameof(KubernetesClusterAgent),
                ServiceLifecycleStage.AfterRuntimeGrainServices,
                OnStart,
                OnStop);
        }

        private async Task OnStart(CancellationToken cancellation)
        {
            var attempts = 0;
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    await AddClusterOptionsToPodLabels(cancellation);

                    // Find the currently known cluster members first, before interrogating Kubernetes
                    await _clusterMembershipService.Refresh();
                    var snapshot = _clusterMembershipService.CurrentSnapshot.Members;

                    // Find the pods which correspond to this cluster
                    var pods = await _client.ListNamespacedPodAsync(
                        namespaceParameter: _podNamespace,
                        labelSelector: _podLabelSelector,
                        cancellationToken: cancellation);
                    var clusterPods = new HashSet<string> { _podName };
                    foreach (var pod in pods.Items)
                    {
                        clusterPods.Add(pod.Metadata.Name);
                    }

                    var known = new HashSet<string>();
                    var knownMap = new Dictionary<string, ClusterMember>();
                    known.Add(_podName);
                    foreach (var member in snapshot.Values)
                    {
                        if (member.Status == SiloStatus.Dead)
                        {
                            continue;
                        }

                        known.Add(member.Name);
                        knownMap[member.Name] = member;
                    }

                    var unknownPods = new List<string>(clusterPods.Except(known));
                    unknownPods.Sort();
                    foreach (var pod in unknownPods)
                    {
                        _logger.LogWarning("Pod {PodName} does not correspond to any known silos", pod);

                        // Delete the pod once it has been active long enough?
                    }

                    var unmatched = new List<string>(known.Except(clusterPods));
                    unmatched.Sort();
                    foreach (var pod in unmatched)
                    {
                        var siloAddress = knownMap[pod];
                        if (siloAddress.Status is not SiloStatus.Active)
                        {
                            continue;
                        }

                        _logger.LogWarning("Silo {SiloAddress} does not correspond to any known pod. Marking it as dead.", siloAddress);
                        await _clusterMembershipService.TryKill(siloAddress.SiloAddress);
                    }

                    break;
                }
                catch (HttpOperationException exception) when (exception.Response.StatusCode is System.Net.HttpStatusCode.Forbidden)
                {
                    _logger.LogError(exception, $"Unable to monitor pods due to insufficient permissions. Ensure that this pod has an appropriate Kubernetes role binding. Here is an example role binding:\n{ExampleRoleBinding}");
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Error while initializing Kubernetes cluster agent");
                    if (++attempts > _options.CurrentValue.MaxKubernetesApiRetryAttempts)
                    {
                        throw;
                    }

                    await Task.Delay(1000, cancellation);
                }
            }

            // Start monitoring loop
            ThreadPool.UnsafeQueueUserWorkItem(_ => _runTask = Task.WhenAll(Task.Run(MonitorOrleansClustering), Task.Run(MonitorKubernetesPods)), null);
        }

        private async Task AddClusterOptionsToPodLabels(CancellationToken cancellation)
        {
            // Propagate our configured cluster membership options to our pod
            var thisPod = await _client.ReadNamespacedPodAsync(_podName, namespaceParameter: _podNamespace, cancellationToken: cancellation);

            var labels = thisPod.Labels();
            if (labels is null
                || !labels.TryGetValue(KubernetesHostingOptions.ServiceIdLabel, out var sidVal) || !string.Equals(_clusterOptions.ServiceId, sidVal, StringComparison.Ordinal)
                || !labels.TryGetValue(KubernetesHostingOptions.ClusterIdLabel, out var cidVal) || !string.Equals(_clusterOptions.ClusterId, cidVal, StringComparison.Ordinal))
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

        public async Task OnStop(CancellationToken cancellationToken)
        {
            _shutdownToken.Cancel();
            _enableMonitoring = false;
            _pauseMonitoringSemaphore.Release();

            if (_runTask is not null)
            {
                await Task.WhenAny(_runTask, Task.Delay(TimeSpan.FromMinutes(1), cancellationToken));
            }
        }

        private async Task MonitorOrleansClustering()
        {
            var previous = _clusterMembershipService.CurrentSnapshot;
            while (!_shutdownToken.IsCancellationRequested)
            {
                try
                {
                    await foreach (var update in _clusterMembershipService.MembershipUpdates.WithCancellation(_shutdownToken.Token))
                    {
                        // Determine which silos should be monitoring Kubernetes
                        var chosenSilos = _clusterMembershipService.CurrentSnapshot.Members.Values
                            .Where(s => s.Status == SiloStatus.Active)
                            .OrderBy(s => s.SiloAddress)
                            .Take(_options.CurrentValue.MaxAgents)
                            .ToList();

                        if (!_enableMonitoring && chosenSilos.Any(s => s.SiloAddress.Equals(_localSiloDetails.SiloAddress)))
                        {
                            _enableMonitoring = true;
                            _pauseMonitoringSemaphore.Release(1);
                        }
                        else if (_enableMonitoring)
                        {
                            _enableMonitoring = false;
                        }

                        if (_enableMonitoring && _options.CurrentValue.DeleteDefunctSiloPods)
                        {
                            var delta = update.CreateUpdate(previous);
                            foreach (var change in delta.Changes)
                            {
                                if (change.SiloAddress.Equals(_localSiloDetails.SiloAddress))
                                {
                                    // Ignore all changes for this silo
                                    continue;
                                }

                                if (change.Status == SiloStatus.Dead)
                                {
                                    try
                                    {
                                        if (_logger.IsEnabled(LogLevel.Information))
                                        {
                                            _logger.LogInformation("Silo {SiloAddress} is dead, proceeding to delete the corresponding pod, {PodName}, in namespace {PodNamespace}", change.SiloAddress, change.Name, _podNamespace);
                                        }

                                        await _client.DeleteNamespacedPodAsync(change.Name, _podNamespace);
                                    }
                                    catch (Exception exception)
                                    {
                                        _logger.LogError(exception, "Error deleting pod {PodName} in namespace {PodNamespace} corresponding to defunct silo {SiloAddress}", change.Name, _podNamespace, change.SiloAddress);
                                    }
                                }
                            }
                        }

                        previous = update;
                    }
                }
                catch (Exception exception) when (!(_shutdownToken.IsCancellationRequested && (exception is TaskCanceledException || exception is OperationCanceledException)))
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug(exception, "Error while monitoring cluster changes");
                    }

                    if (!_shutdownToken.IsCancellationRequested)
                    {
                        await Task.Delay(5000);
                    }
                }
            }
        }

        private async Task MonitorKubernetesPods()
        {
            while (!_shutdownToken.IsCancellationRequested)
            {
                try
                {
                    if (!_enableMonitoring)
                    {
                        // Wait on the semaphore to avoid spinning in a tight loop.
                        await _pauseMonitoringSemaphore.WaitAsync();
                        continue;
                    }

                    if (_shutdownToken.IsCancellationRequested)
                    {
                        break;
                    }

                    var pods = await _client.CoreV1.ListNamespacedPodWithHttpMessagesAsync(
                        namespaceParameter: _podNamespace,
                        labelSelector: _podLabelSelector,
                        watch: true,
                        cancellationToken: _shutdownToken.Token);

                    await foreach (var (eventType, pod) in pods.WatchAsync<V1PodList, V1Pod>(_shutdownToken.Token))
                    {
                        if (!_enableMonitoring || _shutdownToken.IsCancellationRequested)
                        {
                            break;
                        }

                        if (string.Equals(pod.Metadata.Name, _podName, StringComparison.Ordinal))
                        {
                            // Never declare ourselves dead this way.
                            continue;
                        }

                        if (eventType == WatchEventType.Modified)
                        {
                            // TODO: Remember silo addresses for pods that are restarting/terminating
                        }

                        if (eventType == WatchEventType.Deleted)
                        {
                            if (this.TryMatchSilo(pod, out var member) && member.Status != SiloStatus.Dead)
                            {
                                if (_logger.IsEnabled(LogLevel.Information))
                                {
                                    _logger.LogInformation("Declaring server {Silo} dead since its corresponding pod, {Pod}, has been deleted", member.SiloAddress, pod.Metadata.Name);
                                }

                                await _clusterMembershipService.TryKill(member.SiloAddress);
                            }
                        }
                    }

                    if (_enableMonitoring && !_shutdownToken.IsCancellationRequested)
                    {
                        if (_logger.IsEnabled(LogLevel.Debug))
                        {
                            _logger.LogDebug("Unexpected end of stream from Kubernetes API. Will try again.");
                        }

                        await Task.Delay(5000);
                    }
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Error monitoring Kubernetes pods");
                    if (!_shutdownToken.IsCancellationRequested)
                    {
                        await Task.Delay(5000);
                    }
                }
            }
        }

        private bool TryMatchSilo(V1Pod pod, out ClusterMember server)
        {
            var snapshot = _clusterMembershipService.CurrentSnapshot;
            foreach (var member in snapshot.Members)
            {
                if (string.Equals(member.Value.Name, pod.Metadata.Name, StringComparison.Ordinal))
                {
                    server = member.Value;
                    return true;
                }
            }

            server = default;
            return false;
        }
    }
}
