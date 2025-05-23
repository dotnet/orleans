using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Placement;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Internal;
using Orleans.Runtime.Placement.Filtering;
using Orleans.Runtime.Versions;

namespace Orleans.Runtime.Placement
{
    /// <summary>
    /// Central point for placement decisions.
    /// </summary>
    internal partial class PlacementService : IPlacementContext
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
        private readonly PlacementFilterStrategyResolver _filterStrategyResolver;
        private readonly PlacementFilterDirectorResolver _placementFilterDirectoryResolver;

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
            PlacementStrategyResolver strategyResolver,
            PlacementFilterStrategyResolver filterStrategyResolver,
            PlacementFilterDirectorResolver placementFilterDirectoryResolver)
        {
            LocalSilo = localSiloDetails.SiloAddress;
            _strategyResolver = strategyResolver;
            _directorResolver = directorResolver;
            _filterStrategyResolver = filterStrategyResolver;
            _placementFilterDirectoryResolver = placementFilterDirectoryResolver;
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
            if (message.IsTargetFullyAddressed) return Task.CompletedTask;
            if (message.TargetGrain.IsDefault) ThrowMissingAddress();

            var grainId = message.TargetGrain;
            if (_grainLocator.TryLookupInCache(grainId, out var result) && CachedAddressIsValid(message, result))
            {
                LogDebugFoundAddress(result, grainId, message);
                SetMessageTargetPlacement(message, result.SiloAddress);
                return Task.CompletedTask;
            }

            LogDebugLookingUpAddress(grainId, message);
            var worker = _workers[grainId.GetUniformHashCode() % PlacementWorkerCount];
            return worker.AddressMessage(message);

            static void ThrowMissingAddress() => throw new InvalidOperationException("Cannot address a message without a target");
        }

        private void SetMessageTargetPlacement(Message message, SiloAddress targetSilo)
        {
            message.TargetSilo = targetSilo;
            LogTraceAddressMessageSelectTarget(message);
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

            var filters = _filterStrategyResolver.GetPlacementFilterStrategies(grainType);
            if (filters.Length > 0)
            {
                IEnumerable<SiloAddress> filteredSilos = compatibleSilos;
                foreach (var placementFilter in filters)
                {
                    var director = _placementFilterDirectoryResolver.GetFilterDirector(placementFilter);
                    filteredSilos = director.Filter(placementFilter, target, filteredSilos);
                }

                compatibleSilos = filteredSilos.ToArray();
            }

            if (compatibleSilos.Length == 0)
            {
                var allWithType = _grainInterfaceVersions.GetSupportedSilos(grainType).Result;
                var versions = _grainInterfaceVersions.GetSupportedSilos(target.InterfaceType, target.InterfaceVersion).Result;
                var allWithTypeString = string.Join(", ", allWithType.Select(s => s.ToString())) is string withGrain && !string.IsNullOrWhiteSpace(withGrain) ? withGrain : "none";
                var allWithInterfaceString = string.Join(", ", versions.Select(s => s.ToString())) is string withIface && !string.IsNullOrWhiteSpace(withIface) ? withIface : "none";
                throw new OrleansException(
                    $"No active nodes are compatible with grain {grainType} and interface {target.InterfaceType} version {target.InterfaceVersion}. "
                    + $"Known nodes with grain type: {allWithTypeString}. "
                    + $"All known nodes compatible with interface version: {allWithInterfaceString}");
            }

            return compatibleSilos;
        }

        public SiloAddress[] AllActiveSilos
        {
            get
            {
                var result = _siloStatusOracle.GetApproximateSiloStatuses(true).Keys.ToArray();
                if (result.Length > 0) return result;

                LogWarningAllActiveSilos();
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
                { Count: > 0 } cacheUpdates => CachedAddressIsValidCore(message, cachedAddress, cacheUpdates),
                _ => true
            };

            [MethodImpl(MethodImplOptions.NoInlining)]
            bool CachedAddressIsValidCore(Message message, GrainAddress cachedAddress, List<GrainAddressCacheUpdate> cacheUpdates)
            {
                var resultIsValid = true;
                LogDebugInvalidatingCachedEntries(cacheUpdates.Count, message);

                foreach (var update in cacheUpdates)
                {
                    // Invalidate/update cache entries while we are examining them.
                    var invalidAddress = update.InvalidGrainAddress;
                    var validAddress = update.ValidGrainAddress;
                    _grainLocator.UpdateCache(update);

                    if (cachedAddress.Matches(validAddress))
                    {
                        resultIsValid = true;
                    }
                    else if (cachedAddress.Matches(invalidAddress))
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
            private readonly SingleWaiterAutoResetEvent _workSignal = new() { RunContinuationsAsynchronously = true };
            private readonly ILogger _logger;
#pragma warning disable IDE0052 // Remove unread private members. Justification: retained for debugging purposes
            private readonly Task _processLoopTask;
#pragma warning restore IDE0052 // Remove unread private members
            private readonly object _lockObj = new();
            private readonly PlacementService _placementService;
            private List<(Message Message, TaskCompletionSource Completion)> _messages = new();

            public PlacementWorker(PlacementService placementService)
            {
                _logger = placementService._logger;
                _placementService = placementService;

                using var _ = new ExecutionContextSuppressor();
                _processLoopTask = Task.Run(ProcessLoop);
            }

            public Task AddressMessage(Message message)
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                lock (_lockObj)
                {
                    _messages ??= new();
                    _messages.Add((message, completion));
                }

                _workSignal.Signal();
                return completion.Task;
            }

            private List<(Message Message, TaskCompletionSource Completion)> GetMessages()
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
                                var workItem = GetOrAddWorkItem(target);
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
                        LogWarnInPlacementWorker(_logger, exception);
                    }

                    await _workSignal.WaitAsync();
                }

                GrainPlacementWorkItem GetOrAddWorkItem(GrainId target)
                {
                    ref var workItem = ref CollectionsMarshal.GetValueRefOrAddDefault(_inProgress, target, out _);
                    workItem ??= new();
                    return workItem;
                }
            }

            private void AddressWaitingMessages(GrainPlacementWorkItem completedWorkItem)
            {
                var resultTask = completedWorkItem.Result;
                var messages = completedWorkItem.Messages;

                try
                {
                    var siloAddress = resultTask.GetAwaiter().GetResult();
                    foreach (var message in messages)
                    {
                        _placementService.SetMessageTargetPlacement(message.Message, siloAddress);
                        message.Completion.TrySetResult();
                    }
                }
                catch (Exception exception)
                {
                    var originalException = exception switch
                    {
                        AggregateException ae when ae.InnerExceptions.Count == 1 => ae.InnerException,
                        _ => exception,
                    };

                    foreach (var message in messages)
                    {
                        message.Completion.TrySetException(originalException);
                    }
                }

                messages.Clear();
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
                _placementService._grainLocator.UpdateCache(targetGrain, siloAddress);
                return siloAddress;
            }

            private class GrainPlacementWorkItem
            {
                public List<(Message Message, TaskCompletionSource Completion)> Messages { get; } = new();

                public Task<SiloAddress> Result { get; set; }
            }
        }

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Found address {Address} for grain {GrainId} in cache for message {Message}"
        )]
        private partial void LogDebugFoundAddress(GrainAddress address, GrainId grainId, Message message);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Looking up address for grain {GrainId} for message {Message}"
        )]
        private partial void LogDebugLookingUpAddress(GrainId grainId, Message message);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "AddressMessage Placement SelectTarget {Message}"
        )]
        private partial void LogTraceAddressMessageSelectTarget(Message message);

        [LoggerMessage(
            EventId = (int)ErrorCode.Catalog_GetApproximateSiloStatuses,
            Level = LogLevel.Warning,
            Message = "AllActiveSilos SiloStatusOracle.GetApproximateSiloStatuses empty"
        )]
        private partial void LogWarningAllActiveSilos();

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Invalidating {Count} cached entries for message {Message}"
        )]
        private partial void LogDebugInvalidatingCachedEntries(int count, Message message);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Error in placement worker."
        )]
        private static partial void LogWarnInPlacementWorker(ILogger logger, Exception exception);
    }
}
