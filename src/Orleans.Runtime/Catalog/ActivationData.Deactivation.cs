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

    private void DeactivateStuckActivation()
    {
        IsStuckProcessingMessage = true;
        var msg = $"Activation {this} has been processing request {_requests.BlockingRequest} for {_requests.BusyDuration.Elapsed} and is likely stuck.";
        var reason = new DeactivationReason(DeactivationReasonCode.ActivationUnresponsive, msg);

        // Mark the grain as deactivating so that messages are forwarded instead of being invoked
        Deactivate(reason, cancellationToken: default);

        // Try to remove this activation from the catalog and directory
        // This leaves this activation dangling, stuck processing the current request until it eventually completes
        // (which likely will never happen at this point, since if the grain was deemed stuck then there is probably some kind of
        // application bug, perhaps a deadlock)
        UnregisterMessageTarget();
        _shared.InternalRuntime.GrainLocator.Unregister(Address, UnregistrationCause.Force).Ignore();
    }

    void IGrainTimerRegistry.OnTimerCreated(IGrainTimer timer)
    {
        lock (_lock)
        {
            Timers ??= new HashSet<IGrainTimer>();
            Timers.Add(timer);
        }
    }

    void IGrainTimerRegistry.OnTimerDisposed(IGrainTimer timer)
    {
        lock (_lock) // need to lock since dispose can be called on finalizer thread, outside grain context (not single threaded).
        {
            if (Timers is null)
            {
                return;
            }

            Timers.Remove(timer);
        }
    }

    private void DisposeTimers()
    {
        lock (_lock)
        {
            if (Timers is null)
            {
                return;
            }

            // Need to set Timers to null since OnTimerDisposed mutates the timers set if it is not null.
            var timers = Timers;
            Timers = null;

            // Dispose all timers.
            foreach (var timer in timers)
            {
                timer.Dispose();
            }
        }
    }

    /// <summary>
    /// Completes the deactivation process.
    /// </summary>
    private static async Task FinishDeactivating(
        ActivationData activation,
        Command.Deactivate deactivateCommand,
        CancellationToken cancellationToken)
    {
        using var _ = deactivateCommand.Activity;

        var deactivationMetrics = CatalogInstruments.DeactivationMetricTracker.Start(activation._shared.CatalogInstruments);
        var migrating = false;
        var encounteredError = false;
        try
        {
            try
            {
                LogCompletingDeactivation(activation._shared.Logger, activation);

                // Stop timers from firing.
                activation.DisposeTimers();

                // If the grain was valid when deactivation started, call OnDeactivateAsync.
                if (deactivateCommand.PreviousState == ActivationState.Valid)
                {
                    if (activation.GrainInstance is IGrainBase grainBase)
                    {
                        // Start a span for OnDeactivateAsync execution

                        using var onDeactivateSpan = deactivateCommand.Activity is not null
                            ? ActivitySources.LifecycleGrainSource.StartActivity(ActivityNames.OnDeactivate, ActivityKind.Internal, parentContext:deactivateCommand.Activity.Context)
                            : ActivitySources.LifecycleGrainSource.StartActivity(ActivityNames.OnDeactivate, ActivityKind.Internal);
                        if (onDeactivateSpan is { IsAllDataRequested: true })
                        {
                            onDeactivateSpan.SetTag(ActivityTagKeys.GrainId, activation.GrainId.ToString());
                            onDeactivateSpan.SetTag(ActivityTagKeys.GrainType, activation._shared.GrainTypeName ?? activation.GrainInstance.GetType().FullName);
                            onDeactivateSpan.SetTag(ActivityTagKeys.SiloId, activation._shared.Runtime.SiloAddress.ToString());
                            onDeactivateSpan.SetTag(ActivityTagKeys.ActivationId, activation.ActivationId.ToString());
                            onDeactivateSpan.SetTag(ActivityTagKeys.DeactivationReason, activation.DeactivationReason.ToString());
                        }

                        try
                        {
                            LogBeforeOnDeactivateAsync(activation._shared.Logger, activation);

                            await grainBase.OnDeactivateAsync(activation.DeactivationReason, cancellationToken).WaitAsync(cancellationToken);

                            LogAfterOnDeactivateAsync(activation._shared.Logger, activation);
                        }
                        catch (Exception exception)
                        {
                            LogErrorInGrainMethod(activation._shared.Logger, exception, nameof(IGrainBase.OnDeactivateAsync), activation);
                            SetActivityError(onDeactivateSpan, exception, ActivityErrorEvents.OnDeactivateFailed);

                            // Swallow the exception and continue with deactivation.
                            encounteredError = true;
                        }
                    }
                }

                try
                {
                    if (activation._lifecycle is { } lifecycle)
                    {
                        // Stops the lifecycle stages which were previously started.
                        // Stages which were never started are ignored.
                        await lifecycle.OnStop(cancellationToken).WaitAsync(cancellationToken);
                    }
                }
                catch (Exception exception)
                {
                    LogErrorStoppingLifecycle(activation._shared.Logger, exception, activation);

                    // Swallow the exception and continue with deactivation.
                    encounteredError = true;
                }

                if (!encounteredError
                    && activation.DehydrationContext is { } context
                    && activation._shared.MigrationManager is { } migrationManager
                    && !cancellationToken.IsCancellationRequested)
                {
                    migrating = await StartMigrationAsync(activation, context, migrationManager, cancellationToken);
                }

                // If the instance is being deactivated due to a directory failure, we should not unregister it.
                var isDirectoryFailure = activation.DeactivationReason.ReasonCode is DeactivationReasonCode.DirectoryFailure;

                if (!migrating && activation.IsUsingGrainDirectory && !cancellationToken.IsCancellationRequested && !isDirectoryFailure)
                {
                    // Unregister from directory.
                    // If the grain was migrated, the new activation will perform a check-and-set on the registration itself.
                    try
                    {
                        await activation._shared.InternalRuntime.GrainLocator.Unregister(activation.Address, UnregistrationCause.Force).WaitAsync(cancellationToken);
                    }
                    catch (Exception exception)
                    {
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            LogFailedToUnregisterActivation(activation._shared.Logger, exception, activation);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SetActivityError(deactivateCommand.Activity, ex, "Error in FinishDeactivating");
                LogErrorDeactivating(activation._shared.Logger, ex, activation);
            }

            if (activation.IsStuckDeactivating)
            {
                deactivationMetrics = deactivationMetrics.DeactivateStuckActivation();
                activation._shared.CatalogInstruments.ActivationShutdownViaDeactivateStuckActivation();
            }
            else if (migrating)
            {
                deactivationMetrics = deactivationMetrics.Migration();
                activation._shared.CatalogInstruments.ActivationShutdownViaMigration();
            }
            else if (activation._isInWorkingSet)
            {
                deactivationMetrics = deactivationMetrics.DeactivateOnIdle();
                activation._shared.CatalogInstruments.ActivationShutdownViaDeactivateOnIdle();
            }
            else
            {
                deactivationMetrics = deactivationMetrics.Collection();
                activation._shared.CatalogInstruments.ActivationShutdownViaCollection();
            }

            activation.UnregisterMessageTarget();

            try
            {
                await DisposeAsync(activation);
            }
            catch (Exception exception)
            {
                SetActivityError(deactivateCommand.Activity, exception, "Error in FinishDeactivating");
                LogExceptionDisposing(activation._shared.Logger, exception, activation);
            }

            if (activation.DeactivationStartTime is not null)
            {
                GrainLifecycleEvents.EmitDeactivated(activation, activation.DeactivationReason);
            }

            deactivationMetrics = deactivationMetrics.Record();

            // Signal deactivation
            activation.GetDeactivationCompletionSource().TrySetResult(true);
            activation._messagePump.Signal();
        }
        finally
        {
            deactivationMetrics.RecordIfNeeded();
        }

    }

    private static async ValueTask<bool> StartMigrationAsync(
        ActivationData activation,
        DehydrationContextHolder context,
        IActivationMigrationManager migrationManager,
        CancellationToken cancellationToken)
    {
        try
        {
            var forwardingAddress = activation.ForwardingAddress;
            if (forwardingAddress is null)
            {
                forwardingAddress = await PlaceMigratingGrainAsync(activation, context.RequestContext, cancellationToken);
                if (forwardingAddress is null)
                {
                    return false;
                }

                activation.ForwardingAddress = forwardingAddress;
            }

            // Populate the dehydration context.
            if (context.RequestContext is { } requestContext)
            {
                RequestContextExtensions.Import(requestContext);
            }

            activation.OnDehydrate(context.MigrationContext);

            // Send the dehydration context to the target host.
            await migrationManager.MigrateAsync(forwardingAddress, activation.GrainId, context.MigrationContext).AsTask().WaitAsync(cancellationToken);
            activation._shared.InternalRuntime.GrainLocator.UpdateCache(activation.GrainId, forwardingAddress);
            return true;
        }
        catch (Exception exception)
        {
            LogFailedToMigrateActivation(activation._shared.Logger, exception, activation);
            return false;
        }
    }

    private TaskCompletionSource<bool> GetDeactivationCompletionSource()
    {
        lock (_lock)
        {
            _extras ??= new();
            return _extras.DeactivationTask ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    ValueTask IGrainManagementExtension.DeactivateOnIdle()
    {
        Deactivate(new(DeactivationReasonCode.ApplicationRequested, $"{nameof(IGrainManagementExtension.DeactivateOnIdle)} was called."), CancellationToken.None);
        return default;
    }

    ValueTask IGrainManagementExtension.MigrateOnIdle() => MigrateOnIdleAsync(this);

    private static async ValueTask MigrateOnIdleAsync(ActivationData activation)
    {
        var requestContextData = RequestContext.CallContextData?.Value.Values;
        var selectedAddress = await PlaceMigratingGrainAsync(activation, requestContextData, CancellationToken.None);
        if (selectedAddress is null)
        {
            return;
        }

        // Only migrate if a different silo was selected.
        activation.ForwardingAddress = selectedAddress;
        LogDebugMigrating(activation._shared.Logger, activation.GrainId, selectedAddress);
        activation.Migrate(requestContextData, cancellationToken: CancellationToken.None);
    }

    private static async ValueTask<SiloAddress?> PlaceMigratingGrainAsync(
        ActivationData activation,
        Dictionary<string, object>? requestContextData,
        CancellationToken cancellationToken)
    {
        try
        {
            var placementService = activation._shared.Runtime.ServiceProvider.GetRequiredService<PlacementService>();
            var selectedAddress = await placementService.PlaceGrainAsync(activation.GrainId, requestContextData, activation.PlacementStrategy);

            if (selectedAddress is null)
            {
                // No appropriate silo was selected for this grain.
                LogDebugPlacementStrategyFailedToSelectDestination(activation._shared.Logger, activation.PlacementStrategy, activation.GrainId);
                return null;
            }
            else if (selectedAddress.Equals(activation._shared.Runtime.SiloAddress))
            {
                // This could be because this is the only (compatible) silo for the grain or because the placement director chose this
                // silo for some other reason.
                LogDebugPlacementStrategySelectedCurrentSilo(activation._shared.Logger, activation.PlacementStrategy, activation.GrainId);
                return null;
            }

            return selectedAddress;
        }
        catch (Exception exception)
        {
            LogErrorSelectingMigrationDestination(activation._shared.Logger, exception, activation.GrainId);
            return null;
        }
    }

    private void UnregisterMessageTarget()
    {
        _shared.InternalRuntime.Catalog.UnregisterMessageTarget(this);
    }
}
