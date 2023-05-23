using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Internal;
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
            if (_grainLocator.TryLookupInCache(grainId, out var result) && CachedAddressIsValid(message, result))
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Found address {Address} for grain {GrainId} in cache for message {Message}", result, grainId, message);
                }

                SetMessageTargetPlacement(message, result.SiloAddress);
                return Task.CompletedTask;
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Placing grain {GrainId} for message {Message}", grainId, message);
            }

            var worker = _workers[grainId.GetUniformHashCode() % PlacementWorkerCount];
            return worker.AddressMessage(message);

            static void ThrowMissingAddress() => throw new InvalidOperationException("Cannot address a message without a target");
        }

        private void SetMessageTargetPlacement(Message message, SiloAddress targetSilo)
        {
            message.TargetSilo = targetSilo;
#if DEBUG
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace((int)ErrorCode.Dispatcher_AddressMsg_SelectTarget, "AddressMessage Placement SelectTarget {Message}", message);
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

                _logger.LogWarning((int)ErrorCode.Catalog_GetApproximateSiloStatuses, "AllActiveSilos SiloStatusOracle.GetApproximateSiloStatuses empty");
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool CachedAddressIsValid(Message message, GrainAddress cachedAddress)
        {
            // Verify that the result from the cache has not been invalidated by the message being addressed.
            return message.CacheInvalidationHeader switch
            {
                { Count: > 0 } invalidAddresses => CachedAddressIsValidCore(message, cachedAddress, invalidAddresses),
                _ => true
            };

            [MethodImpl(MethodImplOptions.NoInlining)]
            bool CachedAddressIsValidCore(Message message, GrainAddress cachedAddress, List<GrainAddress> invalidAddresses)
            {
                var resultIsValid = true;
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Invalidating {Count} cached entries for message {Message}", invalidAddresses.Count, message);
                }

                foreach (var address in invalidAddresses)
                {
                    // Invalidate the cache entries while we are examining them.
                    _grainLocator.InvalidateCache(address);
                    if (cachedAddress.Matches(address))
                    {
                        resultIsValid = false;
                    }
                }

                return resultIsValid;
            }
        }

        /// <summary>
        /// Places a grain without considering the grain's existing location, if any.
        /// </summary>
        /// <param name="grainId">The grain id of the grain being placed.</param>
        /// <param name="requestContextData">The request context, which will be available to the placement strategy.</param>
        /// <param name="placementStrategy">The placement strategy to use.</param>
        /// <returns>A location for the new activation.</returns>
        public Task<SiloAddress> PlaceGrainAsync(GrainId grainId, Dictionary<string, object> requestContextData, PlacementStrategy placementStrategy)
        {
            var target = new PlacementTarget(grainId, requestContextData, default, 0);
            var director = _directorResolver.GetPlacementDirector(placementStrategy);
            return director.OnAddActivation(placementStrategy, target, this);
        }

        private class PlacementWorker
        {
            private readonly Dictionary<GrainId, GrainPlacementWorkItem> _inProgress = new();
            private readonly SingleWaiterAutoResetEvent _workSignal = new();
            private readonly ILogger _logger;
#pragma warning disable IDE0052 // Remove unread private members. Justification: retained for debugging purposes
            private readonly Task _processLoopTask;
#pragma warning restore IDE0052 // Remove unread private members
            private readonly object _lockObj = new();
            private readonly PlacementService _placementService;
            private List<(Message Message, TaskCompletionSource<bool> Completion)> _messages = new();

            public PlacementWorker(PlacementService placementService)
            {
                _logger = placementService._logger;
                _placementService = placementService;

                using var _ = new ExecutionContextSuppressor();
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
                Action signalWaiter = _workSignal.Signal;
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
                                    // so if subsequent messages do not agree with the first message's interface
                                    // type or version, then they may be sent to an incompatible silo, which is
                                    // fine since the remote silo will handle that incompatibility.
                                    workItem.Result = GetOrPlaceActivationAsync(message.Message);

                                    // Wake up this processing loop when the task completes
                                    workItem.Result.GetAwaiter().UnsafeOnCompleted(signalWaiter);
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
                                _inProgress.Remove(pair.Key);
                            }
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
                        var siloAddress = resultTask.Result;
                        _placementService.SetMessageTargetPlacement(message.Message, siloAddress);
                        message.Completion.TrySetResult(true);
                    }

                    messages.Clear();
                }
                else
                {
                    foreach (var message in messages)
                    {
                        message.Completion.TrySetException(OriginalException(resultTask.Exception));
                    }

                    messages.Clear();
                }

                static Exception OriginalException(AggregateException exception)
                {
                    if (exception.InnerExceptions.Count == 1)
                    {
                        return exception.InnerException;
                    }

                    return exception;
                }
            }

            private async Task<SiloAddress> GetOrPlaceActivationAsync(Message firstMessage)
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
                    return result.SiloAddress;
                }

                var strategy = _placementService._strategyResolver.GetPlacementStrategy(target.GrainIdentity.Type);
                var director = _placementService._directorResolver.GetPlacementDirector(strategy);
                var siloAddress = await director.OnAddActivation(strategy, target, _placementService);

                // Give the grain locator one last chance to tell us that the grain has already been placed
                if (_placementService._grainLocator.TryLookupInCache(targetGrain, out result) && _placementService.CachedAddressIsValid(firstMessage, result))
                {
                    return result.SiloAddress;
                }

                _placementService._grainLocator.InvalidateCache(targetGrain);
                _placementService._grainLocator.CachePlacementDecision(targetGrain, siloAddress);
                return siloAddress;
            }

            private class GrainPlacementWorkItem
            {
                public List<(Message Message, TaskCompletionSource<bool> Completion)> Messages { get; } = new();

                public Task<SiloAddress> Result { get; set; }
            }
        }
    }
}
