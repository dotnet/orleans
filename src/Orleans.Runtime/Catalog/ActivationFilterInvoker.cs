using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime;

/// <summary>
/// Invokes <see cref="IGrainBase.OnActivateAsync"/> or <see cref="IGrainBase.OnDeactivateAsync"/> through the
/// registered <see cref="IActivationFilter"/> pipeline.
/// </summary>
internal sealed class ActivationFilterInvoker : IActivationContext
{
    private readonly IGrainContext grainContext;
    private readonly List<IActivationFilter> filters;
    private readonly ActivationStage stage;
    private readonly DeactivationReason deactivationReason;
    private readonly CancellationToken cancellationToken;
    private readonly Func<CancellationToken, Task> terminal;
    private int invocationStage;
    private bool terminalInvoked;

    /// <summary>
    /// Initializes a new instance of the <see cref="ActivationFilterInvoker"/> class.
    /// </summary>
    /// <param name="grainContext">The grain context of the activation.</param>
    /// <param name="filters">The activation interceptors.</param>
    /// <param name="stage">Which lifecycle operation is running.</param>
    /// <param name="deactivationReason">The reason for deactivation.</param>
    /// <param name="cancellationToken">The cancellation token for the operation.</param>
    /// <param name="terminal">The grain's activation or deactivation hook, invoked when no filters remain.</param>
    public ActivationFilterInvoker(
        IGrainContext grainContext,
        List<IActivationFilter> filters,
        ActivationStage stage,
        DeactivationReason deactivationReason,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task> terminal)
    {
        this.grainContext = grainContext;
        this.filters = filters;
        this.stage = stage;
        this.deactivationReason = deactivationReason;
        this.cancellationToken = cancellationToken;
        this.terminal = terminal;
    }

    /// <inheritdoc />
    public IGrainContext GrainContext => grainContext;

    /// <inheritdoc />
    public object Grain => grainContext.GrainInstance!;

    /// <inheritdoc />
    public GrainId GrainId => grainContext.GrainId;

    /// <inheritdoc />
    public ActivationStage Stage => stage;

    /// <inheritdoc />
    public DeactivationReason DeactivationReason => deactivationReason;

    /// <inheritdoc />
    public async Task Invoke()
    {
        // Remember the stage this call started at. A downstream call can advance the stage more than once
        // (the grain-filter slot is skipped when the grain does not implement the filter), so restoring by
        // decrementing would leave the stage one step too far. A filter which catches a failure and invokes
        // the pipeline again must retry from the same stage, or it silently skips the next filter.
        var entryStage = invocationStage;
        try
        {
            // Execute each stage in the pipeline. Each successive call to this method will invoke the next stage.
            var numFilters = filters.Count;
            if (invocationStage < numFilters)
            {
                // Call each of the specified interceptors.
                var systemWideFilter = filters[invocationStage];
                invocationStage++;
                await systemWideFilter.Invoke(this);

                // If the terminal step did not run, some filter did not continue the pipeline.
                if (!terminalInvoked)
                {
                    ThrowBrokenFilterChain(systemWideFilter.GetType().Name);
                }

                return;
            }

            if (invocationStage == numFilters)
            {
                invocationStage++;

                // Grain-level invoker, if present.
                if (Grain is IActivationFilter grainClassLevelFilter)
                {
                    await grainClassLevelFilter.Invoke(this);

                    // If the terminal step did not run, the grain did not continue the pipeline.
                    if (!terminalInvoked)
                    {
                        ThrowBrokenFilterChain(Grain.GetType().Name);
                    }

                    return;
                }
            }

            if (invocationStage == numFilters + 1)
            {
                // Finally invoke the grain's activation or deactivation hook.
                invocationStage++;
                terminalInvoked = true;
                await terminal(cancellationToken);
                return;
            }
        }
        finally
        {
            invocationStage = entryStage;
        }

        // If this method has been called more than the expected number of times, that is invalid.
        ThrowInvalidCall();
    }

    private static void ThrowInvalidCall()
    {
        throw new InvalidOperationException(
            $"{nameof(ActivationFilterInvoker)}.{nameof(Invoke)}() received an invalid call.");
    }

    private static void ThrowBrokenFilterChain(string filterName)
    {
        throw new InvalidOperationException($"{nameof(ActivationFilterInvoker)}.{nameof(Invoke)}() invoked a broken filter: {filterName}.");
    }
}
