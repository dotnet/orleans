using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Internal;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Versions;

namespace Orleans.Runtime.Placement
{
    /// <summary>
    /// Central point for placement decisions.
    /// </summary>
    internal class PlacementService : IPlacementContext
    {
        private const int PlacementWorkerCount = 16;
        private readonly PlacementStrategyResolver _strategyResolver;
        private readonly PlacementDirectorResolver _directorResolver;
        private readonly ILogger<PlacementService> _logger;
        private readonly GrainLocator _grainLocator;
        private readonly GrainVersionManifest _grainInterfaceVersions;
        private readonly CachedVersionSelectorManager _versionSelectorManager;
        private readonly ISiloStatusOracle _siloStatusOracle;
        private readonly bool _assumeHomogeneousSilosForTesting;
        private readonly PlacementWorker[] _workers;

        /// <summary>
        /// Create a <see cref="PlacementService"/> instance.
        /// </summary>
        public PlacementService(
            IOptionsMonitor<SiloMessagingOptions> siloMessagingOptions,
            ILocalSiloDetails localSiloDetails,
            ISiloStatusOracle siloStatusOracle,
            ILogger<PlacementService> logger,
            GrainLocator grainLocator,
            GrainVersionManifest grainInterfaceVersions,
            CachedVersionSelectorManager versionSelectorManager,
            PlacementDirectorResolver directorResolver,
            PlacementStrategyResolver strategyResolver)
        {
            LocalSilo = localSiloDetails.SiloAddress;
            _strategyResolver = strategyResolver;
            _directorResolver = directorResolver;
            _logger = logger;
            _grainLocator = grainLocator;
            _grainInterfaceVersions = grainInterfaceVersions;
            _versionSelectorManager = versionSelectorManager;
            _siloStatusOracle = siloStatusOracle;
            _assumeHomogeneousSilosForTesting = siloMessagingOptions.CurrentValue.AssumeHomogenousSilosForTesting;
            _workers = new PlacementWorker[PlacementWorkerCount];
            for (var i = 0; i < PlacementWorkerCount; i++)
            {
                _workers[i] = new(this);
            }
        }

        public SiloAddress LocalSilo { get; }

        public SiloStatus LocalSiloStatus => _siloStatusOracle.CurrentStatus;

        /// <summary>
        /// Gets or places an activation.
        /// </summary>
        public Task AddressMessage(Message message)
        {
            if (message.IsFullyAddressed) return Task.CompletedTask;
            if (message.TargetGrain.IsDefault) ThrowMissingAddress();

            var grainId = message.TargetGrain;
            if (_grainLocator.TryLookupInCache(grainId, out var result))
            {
                SetMessageTargetPlacement(message, result.ActivationId, result.SiloAddress);
                return Task.CompletedTask;
            }

            var worker = _workers[grainId.GetUniformHashCode() % PlacementWorkerCount];
            return worker.AddressMessage(message);

            [MethodImpl(MethodImplOptions.NoInlining)]
            static void ThrowMissingAddress() => throw new InvalidOperationException("Cannot address a message without a target");
        }

        private void SetMessageTargetPlacement(Message message, ActivationId activationId, SiloAddress targetSilo)
        {
            message.TargetActivation = activationId;
            message.TargetSilo = targetSilo;
#if DEBUG
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.Trace(ErrorCode.Dispatcher_AddressMsg_SelectTarget, "AddressMessage Placement SelectTarget {0}", message);
#endif
        }

        public SiloAddress[] GetCompatibleSilos(PlacementTarget target)
        {
            // For test only: if we have silos that are not yet in the Cluster TypeMap, we assume that they are compatible
            // with the current silo
            if (_assumeHomogeneousSilosForTesting)
            {
                return AllActiveSilos;
            }

            var grainType = target.GrainIdentity.Type;
            var silos = target.InterfaceVersion > 0
                ? _versionSelectorManager.GetSuitableSilos(grainType, target.InterfaceType, target.InterfaceVersion).SuitableSilos
                : _grainInterfaceVersions.GetSupportedSilos(grainType).Result;

            var compatibleSilos = silos.Intersect(AllActiveSilos).ToArray();
            if (compatibleSilos.Length == 0)
            {
                var allWithType = _grainInterfaceVersions.GetSupportedSilos(grainType).Result;
                var versions = _grainInterfaceVersions.GetSupportedSilos(target.InterfaceType, target.InterfaceVersion).Result;
                var allWithTypeString = string.Join(", ", allWithType.Select(s => s.ToString())) is string withGrain && !string.IsNullOrWhiteSpace(withGrain) ? withGrain : "none";
                var allWithInterfaceString = string.Join(", ", versions.Select(s => s.ToString())) is string withIface && !string.IsNullOrWhiteSpace(withIface) ? withIface : "none";
                throw new OrleansException(
                    $"No active nodes are compatible with grain {grainType} and interface {target.InterfaceType} version {target.InterfaceVersion}. "
                    + $"Known nodes with grain type: {allWithTypeString}. "
                    + $"All known nodes compatible with interface version: {allWithTypeString}");
            }

            return compatibleSilos;
        }

        public SiloAddress[] AllActiveSilos
        {
            get
            {
                var result = _siloStatusOracle.GetApproximateSiloStatuses(true).Keys.ToArray();
                if (result.Length > 0) return result;

                _logger.Warn(ErrorCode.Catalog_GetApproximateSiloStatuses, "AllActiveSilos SiloStatusOracle.GetApproximateSiloStatuses empty");
                return new SiloAddress[] { LocalSilo };
            }
        }

        public IReadOnlyDictionary<ushort, SiloAddress[]> GetCompatibleSilosWithVersions(PlacementTarget target)
        {
            if (target.InterfaceVersion == 0)
            {
                throw new ArgumentException("Interface version not provided", nameof(target));
            }

            var grainType = target.GrainIdentity.Type;
            var silos = _versionSelectorManager
                .GetSuitableSilos(grainType, target.InterfaceType, target.InterfaceVersion)
                .SuitableSilosByVersion;

            return silos;
        }

        private class PlacementWorker
        {
            private readonly Dictionary<GrainId, GrainPlacementWorkItem> _inProgress = new();
            private readonly SingleWaiterAutoResetEvent _workSignal = new();
            private readonly ILogger _logger;
            private readonly Task _processLoopTask;
            private readonly object _lockObj = new();
            private readonly PlacementService _placementService;
            private List<(Message Message, TaskCompletionSource<bool> Completion)> _messages = new();

            public PlacementWorker(PlacementService placementService)
            {
                _logger = placementService._logger;
                _placementService = placementService;
                _processLoopTask = Task.Run(ProcessLoop);
            }

            public Task AddressMessage(Message message)
            {
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                lock (_lockObj)
                {
                    _messages ??= new();
                    _messages.Add((message, completion));
                }

                _workSignal.Signal();
                return completion.Task;
            }

            private List<(Message Message, TaskCompletionSource<bool> Completion)> GetMessages()
            {
                lock (_lockObj)
                {
                    if (_messages is { Count: > 0 } result)
                    {
                        _messages = null;
                        return result;
                    }

                    return null;
                }
            }

            private async Task ProcessLoop()
            {
                var toRemove = new List<GrainId>();
                while (true)
                {
                    try
                    {
                        // Start processing new requests
                        var messages = GetMessages();
                        if (messages is not null)
                        {
                            foreach (var message in messages)
                            {
                                var target = message.Message.TargetGrain;
                                if (!_inProgress.TryGetValue(target, out var workItem))
                                {
                                    _inProgress[target] = workItem = new();
                                }

                                workItem.Messages.Add(message);
                                if (workItem.Result is null)
                                {
                                    // Note that the first message is used as the target to place the message,
                                    // so if subsequent messsages do not agree with the first message's interface
                                    // type or version, then they may be sent to an incompatible silo, which is
                                    // fine since the remote silo will handle that incompatibility.
                                    workItem.Result = GetOrPlaceActivationAsync(message.Message);

                                    // Wake up this processing loop when the task completes
                                    workItem.Result.SignalOnCompleted(_workSignal);
                                }
                            }
                        }

                        // Complete processing any completed request
                        foreach (var pair in _inProgress)
                        {
                            var workItem = pair.Value;
                            if (workItem.Result.IsCompleted)
                            {
                                AddressWaitingMessages(workItem);
                                toRemove.Add(pair.Key);
                            }
                        }

                        // Clean up after completed requests
                        if (toRemove.Count > 0)
                        {
                            foreach (var grainId in toRemove)
                            {
                                _inProgress.Remove(grainId);
                            }

                            toRemove.Clear();
                        }
                    }
                    catch (Exception exception)
                    {
                        _logger.LogWarning(exception, "Exception in placement worker");
                    }

                    await _workSignal.WaitAsync();
                }
            }

            private void AddressWaitingMessages(GrainPlacementWorkItem completedWorkItem)
            {
                var resultTask = completedWorkItem.Result;
                var messages = completedWorkItem.Messages;
                if (resultTask.IsCompletedSuccessfully)
                {
                    foreach (var message in messages)
                    {
                        var result = resultTask.Result;
                        _placementService.SetMessageTargetPlacement(message.Message, result.ActivationId, result.SiloAddress);
                        message.Completion.TrySetResult(true);
                    }

                    messages.Clear();
                }
                else
                {
                    foreach (var message in messages)
                    {
                        message.Completion.TrySetException(resultTask.Exception.OriginalException());
                    }

                    messages.Clear();
                }
            }

            private async Task<GrainAddress> GetOrPlaceActivationAsync(Message firstMessage)
            {
                await Task.Yield();
                var target = new PlacementTarget(
                    firstMessage.TargetGrain,
                    firstMessage.RequestContextData,
                    firstMessage.InterfaceType,
                    firstMessage.InterfaceVersion);

                var targetGrain = target.GrainIdentity;
                var result = await _placementService._grainLocator.Lookup(targetGrain);
                if (result is not null)
                {
                    return result;
                }

                var strategy = _placementService._strategyResolver.GetPlacementStrategy(target.GrainIdentity.Type);
                var director = _placementService._directorResolver.GetPlacementDirector(strategy);
                var siloAddress = await director.OnAddActivation(strategy, target, _placementService);
                
                // Give the grain locator one last chance to tell us that the grain has already been placed
                if (_placementService._grainLocator.TryLookupInCache(targetGrain, out result))
                {
                    return result;
                }

                ActivationId activationId;
                if (strategy.IsDeterministicActivationId)
                {
                    // Use the grain id as the activation id.
                    activationId = ActivationId.GetDeterministic(target.GrainIdentity);
                }
                else
                {
                    activationId = ActivationId.NewId();
                }

                result = GrainAddress.GetAddress(siloAddress, targetGrain, activationId);
                _placementService._grainLocator.InvalidateCache(targetGrain);
                _placementService._grainLocator.CachePlacementDecision(result);
                return result;
            }

            private class GrainPlacementWorkItem
            {
                public List<(Message Message, TaskCompletionSource<bool> Completion)> Messages { get; } = new();

                public Task<GrainAddress> Result { get; set; }
            }
        }
    }
}
