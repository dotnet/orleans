using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Core.Internal;
using Orleans.Diagnostics;
using Orleans.GrainDirectory;
using Orleans.Internal;
using Orleans.Runtime.Diagnostics;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Placement;
using Orleans.Runtime.Scheduler;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Session;
using Orleans.Serialization.TypeSystem;

namespace Orleans.Runtime;

internal sealed partial class ActivationData
{
    /// <summary>
    /// Check whether this activation is overloaded.
    /// Returns LimitExceededException if overloaded, otherwise <c>null</c>.
    /// </summary>
    /// <returns>Returns LimitExceededException if overloaded, otherwise <c>null</c>.</returns>
    public LimitExceededException? CheckOverloaded()
    {
        string limitName = nameof(SiloMessagingOptions.MaxEnqueuedRequestsHardLimit);
        int maxRequestsHardLimit = _shared.MessagingOptions.MaxEnqueuedRequestsHardLimit;
        int maxRequestsSoftLimit = _shared.MessagingOptions.MaxEnqueuedRequestsSoftLimit;
        if (IsStatelessWorker)
        {
            limitName = nameof(SiloMessagingOptions.MaxEnqueuedRequestsHardLimit_StatelessWorker);
            maxRequestsHardLimit = _shared.MessagingOptions.MaxEnqueuedRequestsHardLimit_StatelessWorker;
            maxRequestsSoftLimit = _shared.MessagingOptions.MaxEnqueuedRequestsSoftLimit_StatelessWorker;
        }

        if (maxRequestsHardLimit <= 0 && maxRequestsSoftLimit <= 0) return null; // No limits are set

        int count = GetRequestCount();

        if (maxRequestsHardLimit > 0 && count > maxRequestsHardLimit) // Hard limit
        {
            LogRejectActivationTooManyRequests(_shared.Logger, count, this, maxRequestsHardLimit);
            return new LimitExceededException(limitName, count, maxRequestsHardLimit, ToString());
        }

        if (maxRequestsSoftLimit > 0 && count > maxRequestsSoftLimit) // Soft limit
        {
            LogWarnActivationTooManyRequests(_shared.Logger, count, this, maxRequestsSoftLimit);
            return null;
        }

        return null;
    }

    public void AnalyzeWorkload(DateTime now, IMessageCenter messageCenter, MessageFactory messageFactory, SiloMessagingOptions options)
    {
        var slowRunningRequestDuration = options.RequestProcessingWarningTime;
        var longQueueTimeDuration = options.RequestQueueDelayWarningTime;

        List<string>? diagnostics = null;
        lock (_lock)
        {
            if (State != ActivationState.Valid)
            {
                return;
            }

            if (_requests.BlockingRequest is { } blockingRequest)
            {
                var message = blockingRequest;
                TimeSpan? timeSinceQueued = default;
                if (_requests.TryGetRunningDuration(message, out var waitTime))
                {
                    timeSinceQueued = waitTime.Elapsed;
                }

                var executionTime = _requests.BusyDuration.Elapsed;
                if (executionTime >= slowRunningRequestDuration && !message.IsLocalOnly)
                {
                    GetStatusList(ref diagnostics);
                    if (timeSinceQueued.HasValue)
                    {
                        diagnostics.Add($"Message {message} was enqueued {timeSinceQueued} ago and has now been executing for {executionTime}.");
                    }
                    else
                    {
                        diagnostics.Add($"Message {message} has been executing for {executionTime}.");
                    }

                    var response = messageFactory.CreateDiagnosticResponseMessage(message, isExecuting: true, isWaiting: false, diagnostics);
                    messageCenter.SendMessage(response);
                }
            }

            foreach (var running in _requests.Running)
            {
                var message = running.Key;
                var runDuration = running.Value;
                if (ReferenceEquals(message, _requests.BlockingRequest) || message.IsLocalOnly)
                {
                    continue;
                }

                // Check how long they've been executing.
                var executionTime = runDuration.Elapsed;
                if (executionTime >= slowRunningRequestDuration)
                {
                    // Interleaving message X has been executing for a long time
                    GetStatusList(ref diagnostics);
                    var messageDiagnostics = new List<string>(diagnostics)
                    {
                        $"Interleaving message {message} has been executing for {executionTime}."
                    };

                    var response = messageFactory.CreateDiagnosticResponseMessage(message, isExecuting: true, isWaiting: false, messageDiagnostics);
                    messageCenter.SendMessage(response);
                }
            }

            var queueLength = 1;
            foreach (var pair in _requests.Waiting)
            {
                var message = pair.Message;
                if (message.IsLocalOnly)
                {
                    continue;
                }

                var queuedTime = pair.QueuedTime.Elapsed;
                if (queuedTime >= longQueueTimeDuration)
                {
                    // Message X has been enqueued on the target grain for Y and is currently position QueueLength in queue for processing.
                    GetStatusList(ref diagnostics);
                    var messageDiagnostics = new List<string>(diagnostics)
                    {
                       $"Message {message} has been enqueued on the target grain for {queuedTime} and is currently position {queueLength} in queue for processing."
                    };

                    var response = messageFactory.CreateDiagnosticResponseMessage(message, isExecuting: false, isWaiting: true, messageDiagnostics);
                    messageCenter.SendMessage(response);
                }

                queueLength++;
            }
        }

        void GetStatusList([NotNull] ref List<string>? diagnostics)
        {
            if (diagnostics is not null) return;

            diagnostics = new List<string>
            {
                ToDetailedString(),
                $"TaskScheduler status: {_workItemGroup.DumpStatus()}"
            };
        }
    }

    public override string ToString() => $"[Activation: {Address.SiloAddress}/{GrainId}{ActivationId}{GetActivationInfoString()} State={State}]";

    internal string ToDetailedString(bool includeExtraDetails = false)
    {
        lock (_lock)
        {
            var currentlyExecuting = includeExtraDetails ? _requests.BlockingRequest : null;
            return @$"[Activation: {Address.SiloAddress}/{GrainId}{ActivationId} {GetActivationInfoString()} State={State} NonReentrancyQueueSize={WaitingCount} NumRunning={_requests.RunningCount} IdlenessTimeSpan={GetIdleness()} CollectionAgeLimit={_shared.CollectionAgeLimit}{(currentlyExecuting != null ? " CurrentlyExecuting=" : null)}{currentlyExecuting}]";
        }
    }

    private string GetActivationInfoString()
    {
        var placement = PlacementStrategy?.GetType().Name;
        var grainTypeName = TryGetGrainTypeName();
        return grainTypeName is null ? $"#Placement={placement}" : $"#GrainType={grainTypeName} Placement={placement}";
    }

    private string? TryGetGrainTypeName()
    {
        return _shared.GrainTypeName ?? GrainInstance switch
        {
            { } grainInstance => RuntimeTypeNameFormatter.Format(grainInstance.GetType()),
            _ => null
        };
    }

    bool IActivationWorkingSetMember.IsCandidateForRemoval(bool wouldRemove)
    {
        const int IdlenessLowerBound = 10_000;
        lock (_lock)
        {
            var inactive = IsInactive && _idleDuration.ElapsedMilliseconds > IdlenessLowerBound;

            // This instance will remain in the working set if it is either not pending removal or if it is currently active.
            _isInWorkingSet = !wouldRemove || !inactive;
            return inactive;
        }
    }
}
