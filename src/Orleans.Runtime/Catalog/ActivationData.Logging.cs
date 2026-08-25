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
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "One or more cancellation callbacks failed."
    )]
    private static partial void LogErrorCancellationCallbackFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = (int)ErrorCode.Catalog_Reject_ActivationTooManyRequests,
        Level = LogLevel.Warning,
        Message = "Overload - {Count} enqueued requests for activation {Activation}, exceeding hard limit rejection threshold of {HardLimit}"
    )]
    private static partial void LogRejectActivationTooManyRequests(ILogger logger, int count, ActivationData activation, int hardLimit);

    [LoggerMessage(
        EventId = (int)ErrorCode.Catalog_Warn_ActivationTooManyRequests,
        Level = LogLevel.Warning,
        Message = "Hot - {Count} enqueued requests for activation {Activation}, exceeding soft limit warning threshold of {SoftLimit}"
    )]
    private static partial void LogWarnActivationTooManyRequests(ILogger logger, int count, ActivationData activation, int softLimit);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Error while cancelling on-going operation '{Operation}'."
    )]
    private static partial void LogErrorCancellingOperation(ILogger logger, Exception exception, object operation);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Migrating {GrainId} to {SiloAddress}"
    )]
    private static partial void LogDebugMigrating(ILogger logger, GrainId grainId, SiloAddress siloAddress);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error while selecting a migration destination for {GrainId}"
    )]
    private static partial void LogErrorSelectingMigrationDestination(ILogger logger, Exception exception, GrainId grainId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Placement strategy {PlacementStrategy} failed to select a destination for migration of {GrainId}"
    )]
    private static partial void LogDebugPlacementStrategyFailedToSelectDestination(ILogger logger, PlacementStrategy placementStrategy, GrainId grainId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Placement strategy {PlacementStrategy} selected the current silo as the destination for migration of {GrainId}"
    )]
    private static partial void LogDebugPlacementStrategySelectedCurrentSilo(ILogger logger, PlacementStrategy placementStrategy, GrainId grainId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error invoking MayInterleave predicate on grain {Grain} for message {Message}"
    )]
    private static partial void LogErrorInvokingMayInterleavePredicate(ILogger logger, Exception exception, ActivationData grain, Message message);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error in ProcessOperationsAsync for grain activation '{Activation}'."
    )]
    private static partial void LogErrorInProcessOperationsAsync(ILogger logger, Exception exception, ActivationData activation);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Rehydrating grain '{GrainContext}' from previous activation."
    )]
    private static partial void LogRehydratingGrain(ILogger logger, ActivationData grainContext);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Ignoring attempt to rehydrate grain '{GrainContext}' in the '{State}' state."
    )]
    private static partial void LogIgnoringRehydrateAttempt(ILogger logger, ActivationData grainContext, ActivationState state);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Previous activation address was {PreviousRegistration}"
    )]
    private static partial void LogPreviousActivationAddress(ILogger logger, GrainAddress previousRegistration);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Rehydrated grain from previous activation"
    )]
    private static partial void LogRehydratedGrain(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error while rehydrating activation"
    )]
    private static partial void LogErrorRehydratingActivation(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Dehydrating grain activation"
    )]
    private static partial void LogDehydratingActivation(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Dehydrated grain activation"
    )]
    private static partial void LogDehydratedActivation(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error while dehydrating activation"
    )]
    private static partial void LogErrorDehydratingActivation(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = (int)ErrorCode.Catalog_RerouteAllQueuedMessages,
        Level = LogLevel.Debug,
        Message = "Rejecting {Count} messages from invalid activation {Activation}."
    )]
    private static partial void LogRejectAllQueuedMessages(ILogger logger, int count, ActivationData activation);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Registering grain '{Grain}' in activation directory. Previous known registration is '{PreviousRegistration}'.")]
    private static partial void LogRegisteringGrain(ILogger logger, ActivationData grain, GrainAddress? previousRegistration);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "The grain directory has an existing entry pointing to a different activation of this grain, '{GrainId}', on this silo: '{PreviousRegistration}'."
            + " This may indicate that the previous activation was deactivated but the directory was not successfully updated."
            + " The directory will be updated to point to this activation."
    )]
    private static partial void LogAttemptToRegisterWithPreviousActivation(ILogger logger, GrainId grainId, GrainAddress previousRegistration);

    [LoggerMessage(
        EventId = (int)ErrorCode.Dispatcher_ExtendedMessageProcessing,
        Level = LogLevel.Warning,
        Message = "Current request has been active for {CurrentRequestActiveTime} for grain {Grain}. Currently executing {BlockingRequest}. Trying to enqueue {Message}.")]
    private static partial void LogWarningDispatcher_ExtendedMessageProcessing(
        ILogger logger,
        TimeSpan currentRequestActiveTime,
        ActivationDataLogValue grain,
        Message blockingRequest,
        Message message);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Abandoning stuck deactivating activation {Activation}. ForwardingAddress={ForwardingAddress}")]
    private static partial void LogWarningAbandoningStuckDeactivatingActivation(ILogger logger, ActivationData activation, SiloAddress? forwardingAddress);

    private readonly struct ActivationDataLogValue(ActivationData activation, bool includeExtraDetails = false)
    {
        public override string ToString() => activation.ToDetailedString(includeExtraDetails);
    }

    [LoggerMessage(
        EventId = (int)ErrorCode.Runtime_Error_100064,
        Level = LogLevel.Warning,
        Message = "Failed to register grain {Grain} in grain directory")]
    private static partial void LogFailedToRegisterGrain(ILogger logger, Exception exception, ActivationData grain);


    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Ignoring activation request for {Grain} because this grain is in the '{State}' state")]
    private static partial void LogIgnoringActivateAttempt(ILogger logger, ActivationData grain, ActivationState state);

    [LoggerMessage(
        EventId = (int)ErrorCode.Catalog_BeforeCallingActivate,
        Level = LogLevel.Debug,
        Message = "Activating grain {Grain}")]
    private static partial void LogActivatingGrain(ILogger logger, ActivationData grain);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error starting lifecycle for activation '{Activation}'")]
    private static partial void LogErrorStartingLifecycle(ILogger logger, Exception exception, ActivationData activation);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error thrown from {MethodName} for activation '{Activation}'")]
    private static partial void LogErrorInGrainMethod(ILogger logger, Exception exception, string methodName, ActivationData activation);

    [LoggerMessage(
        EventId = (int)ErrorCode.Catalog_AfterCallingActivate,
        Level = LogLevel.Debug,
        Message = "Finished activating grain {Grain}")]
    private static partial void LogFinishedActivatingGrain(ILogger logger, ActivationData grain);

    [LoggerMessage(
        EventId = (int)ErrorCode.Catalog_ErrorCallingActivate,
        Level = LogLevel.Error,
        Message = "Error activating grain {Grain}")]
    private static partial void LogErrorActivatingGrain(ILogger logger, Exception exception, ActivationData grain);

    [LoggerMessage(
        EventId = (int)ErrorCode.Catalog_DisposedObjectAccess,
        Level = LogLevel.Warning,
        Message = "Disposed object {ObjectName} accessed in OnActivateAsync for grain {Grain}. Ensure the cancellationToken is passed to all async methods or they have .WaitAsync(cancellationToken) called on them.")]
    private static partial void LogActivationDisposedObjectAccessed(ILogger logger, string objectName, ActivationData grain);

    [LoggerMessage(
        EventId = (int)ErrorCode.Catalog_CancelledActivate,
        Level = LogLevel.Information,
        Message = "Activation was cancelled for {Grain}. CancellationRequested={CancellationRequested}, DeactivationReasonCode={DeactivationReasonCode}, DeactivationReason={DeactivationReason}, ForwardingAddress={ForwardingAddress}"
    )]
    private static partial void LogActivationCancelled(ILogger logger, ActivationData grain, bool cancellationRequested, DeactivationReasonCode deactivationReasonCode, string? deactivationReason, SiloAddress? forwardingAddress);
    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "Completing deactivation of '{Activation}'")]
    private static partial void LogCompletingDeactivation(ILogger logger, ActivationData activation);

    [LoggerMessage(
        EventId = (int)ErrorCode.Catalog_BeforeCallingDeactivate,
        Level = LogLevel.Debug,
        Message = "About to call OnDeactivateAsync for '{Activation}'")]
    private static partial void LogBeforeOnDeactivateAsync(ILogger logger, ActivationData activation);

    [LoggerMessage(
        EventId = (int)ErrorCode.Catalog_AfterCallingDeactivate,
        Level = LogLevel.Debug,
        Message = "Returned from calling '{Activation}' OnDeactivateAsync method")]
    private static partial void LogAfterOnDeactivateAsync(ILogger logger, ActivationData activation);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Failed to unregister activation '{Activation}' from directory")]
    private static partial void LogFailedToUnregisterActivation(ILogger logger, Exception exception, ActivationData activation);

    [LoggerMessage(
        EventId = (int)ErrorCode.Catalog_DeactivateActivation_Exception,
        Level = LogLevel.Warning,
        Message = "Error deactivating '{Activation}'")]
    private static partial void LogErrorDeactivating(ILogger logger, Exception exception, ActivationData activation);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Exception disposing activation '{Activation}'")]
    private static partial void LogExceptionDisposing(ILogger logger, Exception exception, ActivationData activation);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to migrate activation '{Activation}'")]
    private static partial void LogFailedToMigrateActivation(ILogger logger, Exception exception, ActivationData activation);

    private readonly struct FullAddressLogRecord(GrainAddress address)
    {
        public override string ToString() => address.ToFullString();
    }

    [LoggerMessage(
        EventId = (int)ErrorCode.Catalog_DuplicateActivation,
        Level = LogLevel.Debug,
        Message = "Tried to create a duplicate activation {Address}, but we'll use {ForwardingAddress} instead. GrainInstance type is {GrainInstanceType}. Full activation address is {FullAddress}. We have {WaitingCount} messages to forward")]
    private static partial void LogDuplicateActivation(
        ILogger logger,
        GrainAddress address,
        SiloAddress? forwardingAddress,
        Type? grainInstanceType,
        FullAddressLogRecord fullAddress,
        int waitingCount);

    [LoggerMessage(
        EventId = (int)ErrorCode.Catalog_RerouteAllQueuedMessages,
        Level = LogLevel.Debug,
        Message = "Rerouting {NumMessages} messages from invalid grain activation {Grain} to {ForwardingAddress}")]
    private static partial void LogReroutingMessages(ILogger logger, int numMessages, ActivationData grain, SiloAddress forwardingAddress);

    [LoggerMessage(
        EventId = (int)ErrorCode.Catalog_RerouteAllQueuedMessages,
        Level = LogLevel.Debug,
        Message = "Rerouting {NumMessages} messages from invalid grain activation {Grain}")]
    private static partial void LogReroutingMessagesNoForwarding(ILogger logger, int numMessages, ActivationData grain);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Activation of grain {Grain} failed")]
    private static partial void LogActivationFailed(ILogger logger, Exception exception, ActivationData grain);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error in grain message loop"
    )]
    private static partial void LogErrorInGrainMessageLoop(ILogger logger, Exception exception);
}
