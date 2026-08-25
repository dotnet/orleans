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
    private void RehydrateInternal(IRehydrationContext context)
    {
        Activity? rehydrateSpan = null;
        try
        {
            LogRehydratingGrain(_shared.Logger, this);

            var grainMigrationParticipant = GrainInstance as IGrainMigrationParticipant;

            if (grainMigrationParticipant is not null)
            {
                // Start a span for rehydration
                rehydrateSpan = _activationActivity is not null
                    ? ActivitySources.LifecycleGrainSource.StartActivity(ActivityNames.ActivationRehydrate,
                        ActivityKind.Internal, _activationActivity.Context)
                    : ActivitySources.LifecycleGrainSource.StartActivity(ActivityNames.ActivationRehydrate,
                        ActivityKind.Internal);
                rehydrateSpan?.SetTag(ActivityTagKeys.GrainId, GrainId.ToString());
                rehydrateSpan?.SetTag(ActivityTagKeys.GrainType, _shared.GrainTypeName);
                rehydrateSpan?.SetTag(ActivityTagKeys.SiloId, _shared.Runtime.SiloAddress.ToString());
                rehydrateSpan?.SetTag(ActivityTagKeys.ActivationId, ActivationId.ToString());
            }

            lock (_lock)
            {
                if (State != ActivationState.Creating)
                {
                    LogIgnoringRehydrateAttempt(_shared.Logger, this, State);
                    rehydrateSpan?.SetTag(ActivityTagKeys.RehydrateIgnored, true);
                    rehydrateSpan?.SetTag(ActivityTagKeys.RehydrateIgnoredReason, $"State is {State}");
                    return;
                }

                if (context.TryGetValue(GrainAddressMigrationContextKey, out GrainAddress? previousRegistration) &&
                    previousRegistration is not null)
                {
                    PreviousRegistration = previousRegistration;
                    LogPreviousActivationAddress(_shared.Logger, previousRegistration);
                    rehydrateSpan?.SetTag(ActivityTagKeys.RehydratePreviousRegistration,
                        previousRegistration.ToFullString());
                }

                if (_lifecycle is { } lifecycle)
                {
                    foreach (var participant in lifecycle.GetMigrationParticipants())
                    {
                        participant.OnRehydrate(context);
                    }
                }

                grainMigrationParticipant?.OnRehydrate(context);
            }

            LogRehydratedGrain(_shared.Logger);
            rehydrateSpan?.AddEvent(new ActivityEvent("rehydrated"));
        }
        catch (Exception exception)
        {
            LogErrorRehydratingActivation(_shared.Logger, exception);
            SetActivityError(rehydrateSpan, exception, ActivityErrorEvents.RehydrateError);
        }
        finally
        {
            rehydrateSpan?.Dispose();
        }
    }

    private void OnDehydrate(IDehydrationContext context)
    {
        LogDehydratingActivation(_shared.Logger);

        lock (_lock)
        {
            Debug.Assert(context is not null);

            if (IsUsingGrainDirectory)
            {
                context.TryAddValue(GrainAddressMigrationContextKey, Address);
            }
            
            Activity? dehydrateSpan = null;
            try
            {
                // Get the parent activity context from the dehydration context holder (captured when migration was initiated)
                var parentContext = DehydrationContext?.MigrationActivityContext;

                var grainMigrationParticipant = GrainInstance as IGrainMigrationParticipant;

                if (grainMigrationParticipant is not null)
                {
                    // Start a span for dehydration, parented to the migration request that triggered it
                    dehydrateSpan = parentContext.HasValue
                        ? ActivitySources.LifecycleGrainSource.StartActivity(ActivityNames.ActivationDehydrate,
                            ActivityKind.Internal, parentContext.Value)
                        : ActivitySources.LifecycleGrainSource.StartActivity(ActivityNames.ActivationDehydrate,
                            ActivityKind.Internal);
                    if (dehydrateSpan is { IsAllDataRequested: true })
                    {
                        dehydrateSpan.SetTag(ActivityTagKeys.GrainId, GrainId.ToString());
                        dehydrateSpan.SetTag(ActivityTagKeys.GrainType, _shared.GrainTypeName);
                        dehydrateSpan.SetTag(ActivityTagKeys.SiloId, _shared.Runtime.SiloAddress.ToString());
                        dehydrateSpan.SetTag(ActivityTagKeys.ActivationId, ActivationId.ToString());
                        if (ForwardingAddress is { } fwd)
                        {
                            dehydrateSpan.SetTag(ActivityTagKeys.MigrationTargetSilo, fwd.ToString());
                        }
                    }
                }

                // Note that these calls are in reverse order from Rehydrate, not for any particular reason other than symmetry.
                grainMigrationParticipant?.OnDehydrate(context);

                if (_lifecycle is { } lifecycle)
                {
                    foreach (var participant in lifecycle.GetMigrationParticipants())
                    {
                        participant.OnDehydrate(context);
                    }
                }
            }
            catch (Exception exception)
            {
                LogErrorDehydratingActivation(_shared.Logger, exception);
                SetActivityError(dehydrateSpan, exception, ActivityErrorEvents.DehydrateError);
            }
            finally
            {
                dehydrateSpan?.Dispose();
            }
        }

        LogDehydratedActivation(_shared.Logger);
    }

    #region Activation
    public void Rehydrate(IRehydrationContext context)
    {
        ScheduleOperation(new Command.Rehydrate(context));
    }

    public void Activate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken)
    {
        var metrics = CatalogInstruments.ActivationMetricTracker.Start(_shared.CatalogInstruments, IsUsingGrainDirectory);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_shared.InternalRuntime.CollectionOptions.Value.ActivationTimeout);

        ScheduleOperation(new Command.Activate(requestContext, cts, metrics));
    }

    private static async Task ActivateAsync(
        ActivationData activation,
        Dictionary<string, object>? requestContextData,
        CatalogInstruments.ActivationMetricTracker activationMetrics,
        CancellationToken cancellationToken)
    {
        if (activation.State != ActivationState.Creating)
        {
            LogIgnoringActivateAttempt(activation._shared.Logger, activation, activation.State);
            return;
        }

        activation._activationActivity?.AddEvent(new ActivityEvent("activation-start"));
        try
        {
            if (activation.IsUsingGrainDirectory)
            {
                bool success;
                Exception? registrationException;

                // Start directory registration activity as a child of the activation activity
                using (var registerSpan = activation._activationActivity is not null
                    ? ActivitySources.LifecycleGrainSource.StartActivity(ActivityNames.RegisterDirectoryEntry, ActivityKind.Internal, activation._activationActivity.Context)
                    : ActivitySources.LifecycleGrainSource.StartActivity(ActivityNames.RegisterDirectoryEntry, ActivityKind.Internal))
                {
                    registerSpan?.SetTag(ActivityTagKeys.GrainId, activation.GrainId.ToString());
                    registerSpan?.SetTag(ActivityTagKeys.SiloId, activation._shared.Runtime.SiloAddress.ToString());
                    registerSpan?.SetTag(ActivityTagKeys.ActivationId, activation.ActivationId.ToString());
                    registerSpan?.SetTag(ActivityTagKeys.DirectoryPreviousRegistrationPresent,
                        activation.PreviousRegistration is not null);
                    var previousRegistration = activation.PreviousRegistration;
                    var verifiedRecoveryMembershipVersion = 0L;
                    
                    try
                    {
                        while (true)
                        {
                            LogRegisteringGrain(activation._shared.Logger, activation, previousRegistration);

                            var result = await activation._shared.InternalRuntime.GrainLocator
                                .Register(activation.Address, previousRegistration, cancellationToken);
                            if (activation.Address.Matches(result))
                            {
                                activation.Address = result;

                                // If DGD recovery advanced while this registration was being committed, re-register
                                // against the recovered view before this activation can become valid.
                                if (activation._shared.GrainDirectory is DistributedGrainDirectory distributedGrainDirectory)
                                {
                                    var recoveryMembershipVersion = distributedGrainDirectory.RecoveryMembershipVersion;
                                    if (recoveryMembershipVersion > verifiedRecoveryMembershipVersion
                                        && recoveryMembershipVersion > result.MembershipVersion.Value)
                                    {
                                        verifiedRecoveryMembershipVersion = recoveryMembershipVersion;
                                        previousRegistration = result;
                                        activation._activationActivity?.AddEvent(new ActivityEvent("directory-register-retry-recovery"));
                                        registerSpan?.AddEvent(new ActivityEvent("retry-recovery"));
                                        continue;
                                    }
                                }

                                success = true;
                                activation._activationActivity?.AddEvent(new ActivityEvent("directory-register-success"));
                                registerSpan?.AddEvent(new ActivityEvent("success"));
                                registerSpan?.SetTag(ActivityTagKeys.DirectoryRegisteredAddress, result.ToFullString());
                            }
                            else if (result?.SiloAddress is { } registeredSilo &&
                                     registeredSilo.Equals(activation.Address.SiloAddress))
                            {
                                previousRegistration = result;
                                LogAttemptToRegisterWithPreviousActivation(activation._shared.Logger, activation.GrainId, result);
                                activation._activationActivity?.AddEvent(new ActivityEvent("directory-register-retry-previous"));
                                registerSpan?.AddEvent(new ActivityEvent("retry-previous"));
                                continue;
                            }
                            else
                            {
                                activation.ForwardingAddress = result?.SiloAddress;
                                if (activation.ForwardingAddress is { } address)
                                {
                                    activation.DeactivationReason = new(DeactivationReasonCode.DuplicateActivation,
                                        $"This grain is active on another host ({address}).");
                                }

                                success = false;
                                activation._shared.CatalogInstruments.OnActivationConcurrentRegistrationAttempt();
                                LogDuplicateActivation(
                                    activation._shared.Logger,
                                    activation.Address,
                                    activation.ForwardingAddress,
                                    activation.GrainInstance?.GetType(),
                                    new(activation.Address),
                                    activation.WaitingCount);
                                activation._activationActivity?.AddEvent(new ActivityEvent("duplicate-activation"));
                                registerSpan?.AddEvent(new ActivityEvent("duplicate"));
                                if (activation.ForwardingAddress is { } fwd)
                                {
                                    registerSpan?.SetTag(ActivityTagKeys.DirectoryForwardingAddress, fwd.ToString());
                                }
                            }

                            break;
                        }

                        registrationException = null;
                    }
                    catch (Exception exception)
                    {
                        registrationException = exception;
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            LogFailedToRegisterGrain(activation._shared.Logger, registrationException, activation);
                        }

                        success = false;
                        activation._activationActivity?.AddEvent(new ActivityEvent("directory-register-failed"));
                        SetActivityError(registerSpan, exception, ActivityErrorEvents.DirectoryRegisterFailed);
                    }

                }
                if (!success)
                {
                    activation.Deactivate(new(DeactivationReasonCode.DirectoryFailure, registrationException, "Failed to register activation in grain directory."));
                    activationMetrics.DirectoryRegistrationFailed(registrationException, cancellationToken.IsCancellationRequested);

                    // Activation failed.
                    if (registrationException is not null)
                    {
                        SetActivityError(activation._activationActivity, registrationException, ActivityErrorEvents.ActivationCancelled);
                    }
                    else
                    {
                        SetActivityError(activation._activationActivity, ActivityErrorEvents.ActivationCancelled);
                    }

                    return;
                }
            }

            lock (activation._lock)
            {
                activation.SetState(ActivationState.Activating);
            }
            activation._activationActivity?.AddEvent(new ActivityEvent("state-activating"));
            LogActivatingGrain(activation._shared.Logger, activation);

            try
            {
                RequestContextExtensions.Import(requestContextData);
                try
                {
                    if (activation._lifecycle is { } lifecycle)
                    {
                        activation._activationActivity?.AddEvent(new ActivityEvent("lifecycle-start"));
                        await lifecycle.OnStart(cancellationToken).WaitAsync(cancellationToken);
                        activation._activationActivity?.AddEvent(new ActivityEvent("lifecycle-started"));
                    }
                }
                catch (Exception exception)
                {
                    LogErrorStartingLifecycle(activation._shared.Logger, exception, activation);
                    activation._activationActivity?.AddEvent(new ActivityEvent("lifecycle-start-failed"));
                    throw;
                }

                if (activation.GrainInstance is IGrainBase grainBase)
                {
                    // Start a span for OnActivateAsync execution
                    using var onActivateSpan = activation._activationActivity is not null
                        ? ActivitySources.LifecycleGrainSource.StartActivity(ActivityNames.OnActivate, ActivityKind.Internal, activation._activationActivity.Context)
                        : ActivitySources.LifecycleGrainSource.StartActivity(ActivityNames.OnActivate, ActivityKind.Internal);
                    if (onActivateSpan is { IsAllDataRequested: true })
                    {
                        onActivateSpan.SetTag(ActivityTagKeys.GrainId, activation.GrainId.ToString());
                        onActivateSpan.SetTag(ActivityTagKeys.GrainType, activation._shared.GrainTypeName ?? activation.GrainInstance.GetType().FullName);
                        onActivateSpan.SetTag(ActivityTagKeys.SiloId, activation._shared.Runtime.SiloAddress.ToString());
                        onActivateSpan.SetTag(ActivityTagKeys.ActivationId, activation.ActivationId.ToString());
                    }

                    try
                    {
                        await grainBase.OnActivateAsync(cancellationToken).WaitAsync(cancellationToken);
                    }
                    catch (Exception exception)
                    {
                        if (cancellationToken.IsCancellationRequested && exception is ObjectDisposedException or OperationCanceledException)
                        {
                            activation._shared.CatalogInstruments.OnActivationFailedToActivate();

                            // This captures the case where user code in OnActivateAsync doesn't use the passed cancellation token
                            // and makes a call that tries to resolve the scoped IServiceProvider or other type that has been disposed because of cancellation,
                            // or a direct OperationCanceledException from cancellation.
                            if (exception is ObjectDisposedException ode)
                            {
                                LogActivationDisposedObjectAccessed(activation._shared.Logger, ode.ObjectName, activation);
                                activation.Deactivate(
                                    new(DeactivationReasonCode.RuntimeRequested, ode,
                                        $"Disposed object {ode.ObjectName} referenced after cancellation of activation was requested."),
                                    CancellationToken.None);
                            }
                            else
                            {
                                activation.Deactivate(
                                    new(DeactivationReasonCode.RuntimeRequested, exception,
                                        "Activation was cancelled by the runtime."), CancellationToken.None);
                            }

                            SetActivityError(activation._activationActivity, exception, ActivityErrorEvents.ActivationCancelled);
                            LogActivationCancelled(activation._shared.Logger, activation, cancellationToken.IsCancellationRequested,
                                activation.DeactivationReason.ReasonCode, activation.DeactivationReason.Description, activation.ForwardingAddress);
                            activation._activationActivity?.Dispose();
                            activation._activationActivity = null;
                            activationMetrics.Canceled();
                            return;
                        }

                        LogErrorInGrainMethod(activation._shared.Logger, exception, nameof(IGrainBase.OnActivateAsync), activation);
                        SetActivityError(onActivateSpan, exception, ActivityErrorEvents.OnActivateFailed);
                        throw;
                    }
                }

                lock (activation._lock)
                {
                    if (activation.State is ActivationState.Activating)
                    {
                        activation.SetState(ActivationState.Valid);
                        activation._shared.InternalRuntime.ActivationWorkingSet.OnActivated(activation);
                    }
                }
                activation._activationActivity?.AddEvent(new ActivityEvent("state-valid"));
                activation._activationActivity?.Dispose();
                activation._activationActivity = null;

                GrainLifecycleEvents.EmitActivated(activation);

                LogFinishedActivatingGrain(activation._shared.Logger, activation);
                activationMetrics.Succeeded();
            }
            catch (Exception exception)
            {
                activation._shared.CatalogInstruments.OnActivationFailedToActivate();
                activationMetrics.Failed(cancellationToken.IsCancellationRequested);
                var sourceException = (exception as OrleansLifecycleCanceledException)?.InnerException ?? exception;
                LogErrorActivatingGrain(activation._shared.Logger, sourceException, activation);
                if (!cancellationToken.IsCancellationRequested)
                {
                    activation.ScheduleOperation(new Command.Delay(TimeSpan.FromSeconds(5)));
                }
                activation.Deactivate(new(DeactivationReasonCode.ActivationFailed, sourceException, "Failed to activate grain."), CancellationToken.None);
                SetActivityError(activation._activationActivity, ActivityErrorEvents.ActivationFailed);
                activation._activationActivity?.Dispose();
                activation._activationActivity = null;
                return;
            }
        }
        catch (Exception exception)
        {
            LogActivationFailed(activation._shared.Logger, exception, activation);
            activationMetrics.Failed(cancellationToken.IsCancellationRequested);
            activation.Deactivate(new(DeactivationReasonCode.ApplicationError, exception, "Failed to activate grain."), CancellationToken.None);
            SetActivityError(activation._activationActivity, ActivityErrorEvents.ActivationError);
            activation._activationActivity?.Dispose();
            activation._activationActivity = null;
        }
        finally
        {
            activationMetrics.Record();
            activation._messagePump.Signal();
        }
    }

    private static void SetActivityError(Activity? erroredActivity, string? errorEventName)
    {
        if (erroredActivity is { } activity)
        {
            activity.SetStatus(ActivityStatusCode.Error, errorEventName);
        }
    }

    private static void SetActivityError(Activity? erroredActivity, Exception exception, string? errorEventName)
    {
        if (erroredActivity is { } activity)
        {
            activity.SetStatus(ActivityStatusCode.Error, errorEventName);
            activity.SetTag(ActivityTagKeys.ExceptionType, exception.GetType().FullName);
            activity.SetTag(ActivityTagKeys.ExceptionMessage, exception.Message);
        }
    }

    #endregion
}
