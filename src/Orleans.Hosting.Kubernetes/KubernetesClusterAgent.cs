using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Rest;
using Newtonsoft.Json;
using Orleans.Configuration;
using Orleans.Runtime;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Orleans.Hosting.Kubernetes
{
    /// <summary>
    /// Reflects cluster configuration changes between Orleans and Kubernetes.
    /// </summary>
    public sealed class KubernetesClusterAgent : ILifecycleParticipant<ISiloLifecycle>
    {
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
                OnRuntimeInitializeStart,
                OnRuntimeInitializeStop);
        }

        private async Task OnRuntimeInitializeStart(CancellationToken cancellation)
        {
            // Find the currently known cluster members first, before interrogating Kubernetes
            await _clusterMembershipService.Refresh();
            var snapshot = _clusterMembershipService.CurrentSnapshot.Members;

            // Find the pods which correspond to this cluster
            var pods = await _client.ListNamespacedPodAsync(
                namespaceParameter: _podNamespace,
                labelSelector: _podLabelSelector,
                cancellationToken: cancellation);
            var clusterPods = new HashSet<string>();
            clusterPods.Add(_podName);
            foreach (var pod in pods.Items)
            {
                clusterPods.Add(pod.Metadata.Name);
            }

            HashSet<string> known = new HashSet<string>();
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

            var unknown = new List<string>(clusterPods.Except(known));
            unknown.Sort();
            foreach (var pod in unknown)
            {
                _logger.LogWarning("Pod {PodName} does not correspond to any known silos", pod);

                // Delete the pod once it has been active long enough?
            }

            var unmatched = new List<string>(known.Except(clusterPods));
            unmatched.Sort();
            foreach (var pod in unmatched)
            {
                var siloAddress = knownMap[pod];
                _logger.LogWarning("Silo {SiloAddress} does not correspond to any known pod. Marking it as dead.", siloAddress);
                await _clusterMembershipService.TryKill(siloAddress.SiloAddress);
            }

            // Start monitoring loop
            ThreadPool.UnsafeQueueUserWorkItem(_ => _runTask = Task.WhenAll(Task.Run(MonitorOrleansClustering), Task.Run(MonitorKubernetesPods)), null);
        }

        public async Task OnRuntimeInitializeStop(CancellationToken cancellationToken)
        {
            _shutdownToken.Cancel();
            _enableMonitoring = false;
            _pauseMonitoringSemaphore.Release();

            if (_runTask is object)
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
                            _logger.LogInformation("Enabling Kubernetes monitoring");
                            _enableMonitoring = true;
                            _pauseMonitoringSemaphore.Release(1);
                        }
                        else if (_enableMonitoring)
                        {
                            _logger.LogInformation("Pausing Kubernetes monitoring");
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
                                        _logger.LogInformation("Silo {SiloAddress} is dead, proceeding to delete the corresponding pod, {PodName}, in namespace {PodNamespace}", change.SiloAddress, change.Name, _podNamespace);
                                        await _client.DeleteNamespacedPodAsync(change.Name, _podNamespace);
                                    }
                                    catch (Exception exception)
                                    {
                                        _logger.LogError(exception, "Error deleting pod {PodName} in namespace {PodNamespace}", change.Name, _podNamespace);
                                    }
                                }
                            }
                        }

                        previous = update;
                    }
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Error monitoring cluster changes");
                    if (!_shutdownToken.IsCancellationRequested)
                    {
                        await Task.Delay(5000);
                    }
                }
            }
        }

        private async Task MonitorKubernetesPods()
        {
            var jsonSettings = new JsonSerializerSettings
            {
                DefaultValueHandling = DefaultValueHandling.Ignore,
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore,
            };

            while (!_shutdownToken.IsCancellationRequested)
            {
                try
                {
                    if (!_enableMonitoring)
                    {
                        // Pulse the semaphore to avoid spinning in a tight loop.
                        _logger.LogInformation("Waiting for Kubernetes monitoring to be enabled");
                        await _pauseMonitoringSemaphore.WaitAsync();
                        _logger.LogInformation("Woke up after slumber");
                        continue;
                    }

                    if (_shutdownToken.IsCancellationRequested)
                    {
                        _logger.LogInformation("Shutdown1");
                        break;
                    }

                    var pods = await _client.ListNamespacedPodWithHttpMessagesAsync(
                        namespaceParameter: _podNamespace,
                        labelSelector: _podLabelSelector,
                        watch: true,
                        cancellationToken: _shutdownToken.Token);

                    await foreach (var (eventType, pod) in pods.WatchAsync<V1PodList, V1Pod>(_shutdownToken.Token))
                    {
                        if (!_enableMonitoring || _shutdownToken.IsCancellationRequested)
                        {
                            _logger.LogInformation("Break loop");
                            break;
                        }
#if false
                        _logger.LogInformation(
                            "Event: {Event} Pod: {Pod}",
                            eventType.ToString(),
                            JsonConvert.SerializeObject(pod, jsonSettings));
#endif
                        _logger.LogInformation(
                            "Event: {Event} PodName: {PodName}",
                            eventType.ToString(),
                            pod.Metadata.Name);

                        if (string.Equals(pod.Metadata.Name, _podName, StringComparison.Ordinal))
                        {
                            // Never declare ourselves dead this way.
                            continue;
                        }

                        if (eventType == WatchEventType.Modified)
                        {
                            // TODO: Remember silo addresses for pods are restarting/terminating
                        }

                        if (eventType == WatchEventType.Deleted)
                        {
                            if (this.TryMatchSilo(pod, out var member) && member.Status != SiloStatus.Dead)
                            {
                                _logger.LogInformation("Declaring server {Silo} dead since its corresponding pod, {Pod}, has been deleted", member.SiloAddress, pod.Metadata.Name);
                                await _clusterMembershipService.TryKill(member.SiloAddress);
                            }
                        }
                    }

                    if (_enableMonitoring && !_shutdownToken.IsCancellationRequested)
                    {
                        _logger.LogDebug("Unexpected end of stream from Kubernetes API. Will try again.");
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

    internal static class KubernetesExtensions
    {
        public static async IAsyncEnumerable<(WatchEventType EventType, TValue Value)> WatchAsync<TList, TValue>(this HttpOperationResponse<TList> watchList, [EnumeratorCancellation] CancellationToken cancellation)
        {
            Channel<(WatchEventType, TValue)> channel = Channel.CreateUnbounded<(WatchEventType, TValue)>(
                new UnboundedChannelOptions
                {
                    AllowSynchronousContinuations = false,
                    SingleReader = true,
                    SingleWriter = true
                });

            var reader = channel.Reader;
            Watcher<TValue>[] watcher = new Watcher<TValue>[] { default };
            var cancellationRegistration = cancellation.Register(() =>
            {
                _ = channel.Writer.TryComplete();
                watcher[0]?.Dispose();
            });

            watcher[0] = watchList.Watch<TValue, TList>((eventType, value) =>
            {
                _ = channel.Writer.TryWrite((eventType, value));
            },
            exception =>
            {
                _ = channel.Writer.TryComplete(exception);
                cancellationRegistration.Dispose();
            },
            () =>
            {
                _ = channel.Writer.TryComplete();
                cancellationRegistration.Dispose();
            });

            _ = Task.Run(async () =>
            {
                await channel.Reader.Completion.ConfigureAwait(false);
                watcher[0].Dispose();
                cancellationRegistration.Dispose();
            });


            while (await channel.Reader.WaitToReadAsync(cancellation))
            {
                while (reader.TryRead(out var item))
                {
                    yield return item;
                }
            }
        }
    }
}
