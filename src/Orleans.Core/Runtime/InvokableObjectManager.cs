using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Internal;
using Orleans.Serialization;
using Orleans.Serialization.Invocation;

namespace Orleans;

internal sealed partial class InvokableObjectManager
{
    private readonly ConcurrentDictionary<ObserverGrainId, LocalObjectData> localObjects = new ConcurrentDictionary<ObserverGrainId, LocalObjectData>();

    private readonly InterfaceToImplementationMappingCache _interfaceToImplementationMapping;
    private readonly IGrainContext rootGrainContext;
    private readonly IRuntimeClient runtimeClient;
    private readonly ILogger logger;
    private readonly DeepCopier deepCopier;
    private readonly DeepCopier<Response> _responseCopier;
    private readonly MessagingTrace messagingTrace;
    private readonly AdmissionGate _invocations = new();
    private readonly AdmissionGate _cancellations = new();
    private List<IIncomingGrainCallFilter>? _grainCallFilters;

#if NET10_0_OR_GREATER
    // This lock serializes lifecycle transitions such as: register, deregister, stopping, so that
    // _isStopping and _observerDisposals stay consistent with localObjects across multi-step sequences
    // such as: TryDeregister's remove-then-track, or StopAsync's drain-and-snapshot.
    // Note that it does NOT guard the dictionary itself: localObjects remains a CDict because Dispatch reads it on the
    // hot path *without* this lock, relying on CDict's lock-free TryGetValue for concurrent writers.
    private readonly Lock _observerLifecycleLock = new();
#else
    private readonly object _observerLifecycleLock = new();
#endif

    private readonly HashSet<Task> _observerDisposals = [];
    private bool _isStopping;

    private List<IIncomingGrainCallFilter> GrainCallFilters
        => _grainCallFilters ??= [.. runtimeClient.ServiceProvider.GetServices<IIncomingGrainCallFilter>()];

    public InvokableObjectManager(
        IGrainContext rootGrainContext,
        IRuntimeClient runtimeClient,
        DeepCopier deepCopier,
        MessagingTrace messagingTrace,
        DeepCopier<Response> responseCopier,
        InterfaceToImplementationMappingCache interfaceToImplementationMapping,
        ILogger logger)
    {
        this.rootGrainContext = rootGrainContext;
        this.runtimeClient = runtimeClient;
        this.deepCopier = deepCopier;
        this.messagingTrace = messagingTrace;
        _responseCopier = responseCopier;
        _interfaceToImplementationMapping = interfaceToImplementationMapping;
        this.logger = logger;
    }

    public bool TryRegister(IAddressable obj, ObserverGrainId objectId)
    {
        lock (_observerLifecycleLock)
        {
            return !_isStopping && localObjects.TryAdd(objectId, new LocalObjectData(obj, objectId, this));
        }
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "Disposal is fired and tracked instead of performing it inline." +
        "This ensures we have a gated way to ensure disposal is handled if the manager itself starts shutdown while an existing" +
        "dispoal is going on, triggered likely by the deregistration itself.")]
    public bool TryDeregister(ObserverGrainId objectId)
    {
        lock (_observerLifecycleLock)
        {
            if (!localObjects.TryRemove(objectId, out var objectData))
            {
                return false;
            }

            TrackObserverDisposal(objectData);

            return true;
        }
    }

    /// <remarks>Always call this under the lifecycle lock!</remarks>
    private void TrackObserverDisposal(LocalObjectData objectData)
    {
        var task = objectData.DisposeAsync().AsTask();
        if (task.IsCompleted)
        {
            if (task.Exception is { } exception)
            {
                LogErrorDisposingObservers(logger, exception);
            }

            return;
        }

        _observerDisposals.Add(task);

        _ = task.ContinueWith(static (completedTask, state) =>
        {
            var manager = (InvokableObjectManager)state!;

            lock (manager._observerLifecycleLock) // This is fine as due to lock reentracy.
            {
                manager._observerDisposals.Remove(completedTask);
            }

            if (completedTask.Exception is { } exception)
            {
                LogErrorDisposingObservers(manager.logger, exception);
            }
        },
        // 'ExecuteSynchronously' is an optimization because when 'DisposeAsync' completes synchronously (which it will if
        // the observer does not have any IAE-returning methods), then removal from the tracked disposals can occur inline
        // without requiring a thread pool thread hop.
        this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// Closes observer admission and drains invocation processing, including cancellation controls and response handling.
    /// </summary>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Symmetric reasoning as with TryTryDeregister.")]
    public async Task StopAsync()
    {
        lock (_observerLifecycleLock)
        {
            _isStopping = true;
        }

        // Cancellation controls can release admitted work while application admission is closed.
        await _invocations.CloseAsync().ConfigureAwait(false);
        await _cancellations.CloseAsync().ConfigureAwait(false);

        Task[] disposalTasks;

        lock (_observerLifecycleLock)
        {
            // We dispose all registered observers, also cancelling any outstanding enumerators.
            // This will include disposals initiated by concurrent TryDeregister calls as well.
            foreach (var entry in localObjects.ToArray())
            {
                if (localObjects.TryRemove(entry.Key, out var objectData))
                {
                    TrackObserverDisposal(objectData);
                }
            }

            disposalTasks = [.. _observerDisposals];
        }

        try
        {
            await Task.WhenAll(disposalTasks).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogErrorDisposingObservers(logger, exception);
        }
    }

    internal static bool IsCancellationRequest(Message message) =>
        message.BodyObject is IInvokable request && request.GetInterfaceType() == typeof(IGrainCallCancellationExtension);

    private AdmissionGate GetAdmissionGate(Message message) =>
        IsCancellationRequest(message) ? _cancellations : _invocations;

    internal void RejectMessage(Message message)
    {
        LogRejectingMessageDuringShutdown(logger, message);
        if (message.Direction == Message.Directions.Request)
        {
            runtimeClient.SendResponse(message, Response.FromException(
                new SiloUnavailableException("The local Orleans host is shutting down and can no longer accept observer invocations.")));
        }
    }

    public void Dispatch(Message message)
    {
        if (!ObserverGrainId.TryParse(message.TargetGrain, out var observerId))
        {
            LogNotAddressedToAnObserver(logger, message);
            return;
        }

        if (this.localObjects.TryGetValue(observerId, out var objectData))
        {
            objectData.ReceiveMessage(message);
        }
        else
        {
            LogUnexpectedTargetInRequest(logger, message.TargetGrain, message);
            if (message.Direction == Message.Directions.Request)
            {

                runtimeClient.SendResponse(message, Response.FromException(
                    new ObserverNotReachableException($"The target observer = '{observerId}' is not registered.")));
            }
        }
    }

    private readonly struct ObserverGrainIdLogValue(ObserverGrainId observerId)
    {
        public override string ToString() => observerId.ToString();
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Error disposing an observer during shutdown.")]
    private static partial void LogErrorDisposingObservers(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Rejecting observer invocation during shutdown: '{Message}'."
    )]
    private static partial void LogRejectingMessageDuringShutdown(ILogger logger, Message message);

    [LoggerMessage(
        EventId = (int)ErrorCode.ProxyClient_OGC_TargetNotFound_2,
        Level = LogLevel.Error,
        Message = "Message is not addressed to an observer. Message: '{Message}'."
    )]
    private static partial void LogNotAddressedToAnObserver(ILogger logger, Message message);

    [LoggerMessage(
        EventId = (int)ErrorCode.ProxyClient_OGC_TargetNotFound,
        Level = LogLevel.Error,
        Message = "Unexpected target grain in request: {TargetGrain}. Message: '{Message}'."
    )]
    private static partial void LogUnexpectedTargetInRequest(ILogger logger, GrainId targetGrain, Message message);

    [LoggerMessage(
        Level = LogLevel.Warning,
        EventId = (int)ErrorCode.Runtime_Error_100162,
        Message = "Object associated with id '{ObserverId}' has been garbage collected. Deleting object reference and unregistering it. Message: '{Message}'."
    )]
    private static partial void LogObserverGarbageCollected(ILogger logger, ObserverGrainId observerId, Message message);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "InvokeLocalObjectAsync '{Message}' start '{Start}'."
    )]
    private static partial void LogInvokeLocalObjectAsync(ILogger logger, Message message, bool start);

    [LoggerMessage(
        Level = LogLevel.Warning,
        EventId = (int)ErrorCode.ProxyClient_OGC_SendResponseFailed,
        Message = "Error sending a response."
    )]
    private static partial void LogErrorSendingResponse(ILogger logger, Exception exc);

    [LoggerMessage(
        Level = LogLevel.Error,
        EventId = (int)ErrorCode.ProxyClient_OGC_UnhandledExceptionInOneWayInvoke,
        Message = "Exception during invocation of notification '{Request}', interface '{Interface}'. Ignoring exception because this is a one way request."
    )]
    private static partial void LogErrorInvokingOneWayRequest(ILogger logger, Exception exception, string? request, GrainInterfaceType @interface);

    [LoggerMessage(
        Level = LogLevel.Warning,
        EventId = (int)ErrorCode.ProxyClient_OGC_SendExceptionResponseFailed,
        Message = "Error sending an exception response."
    )]
    private static partial void LogErrorSendingExceptionResponse(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Exception during message body deserialization for message: '{Message}'."
    )]
    private static partial void LogErrorDeserializingMessageBody(ILogger logger, Exception exception, Message message);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Exception processing message '{Message}'."
    )]
    private static partial void LogErrorProcessingMessage(ILogger logger, Exception exception, Message message);
}
