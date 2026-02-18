#nullable enable
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains;

/// <summary>
/// Base class for activation cancellation test grains.
/// </summary>
public abstract class ActivationCancellationTestGrainBase : Grain
{
    protected readonly ILogger Logger;
    protected readonly string ActivationId = Guid.NewGuid().ToString();
    protected bool IsActivatedSuccessfully;
    private readonly IGrainRuntime _grainRuntime;

    protected ActivationCancellationTestGrainBase(ILogger logger, IGrainRuntime grainRuntime)
    {
        Logger = logger;
        _grainRuntime = grainRuntime;
    }

    /// <summary>
    /// Gets the TimeProvider from the grain runtime.
    /// </summary>
    protected TimeProvider TimeProvider => _grainRuntime.TimeProvider;

    public Task<string> GetActivationId() => Task.FromResult(ActivationId);

    public Task<bool> IsActivated() => Task.FromResult(IsActivatedSuccessfully);
}

/// <summary>
/// Grain that throws OperationCanceledException during OnActivateAsync when the cancellation token is triggered.
/// This simulates code that properly observes the cancellation token by passing it to async methods.
/// </summary>
public class ActivationCancellation_ThrowsOperationCancelledGrain
    : ActivationCancellationTestGrainBase, IActivationCancellation_ThrowsOperationCancelledGrain
{
    public ActivationCancellation_ThrowsOperationCancelledGrain(ILogger<ActivationCancellation_ThrowsOperationCancelledGrain> logger, IGrainRuntime grainRuntime)
        : base(logger, grainRuntime)
    {
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        Logger.LogDebug("OnActivateAsync starting for {GrainType}", GetType().Name);

        // Check if we should simulate a delay that would cause cancellation
        if (RequestContext.Get("delay_activation_ms") is int delayMs && delayMs > 0)
        {
            Logger.LogDebug("Delaying activation by {DelayMs}ms", delayMs);
            await Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken);
        }

        IsActivatedSuccessfully = true;
        Logger.LogDebug("OnActivateAsync completed successfully for {GrainType}", GetType().Name);
        await base.OnActivateAsync(cancellationToken);
    }
}

/// <summary>
/// Grain that throws ObjectDisposedException during OnActivateAsync.
/// This simulates code that doesn't observe the cancellation token but tries to access services
/// that have been disposed after cancellation.
/// </summary>
public class ActivationCancellation_ThrowsObjectDisposedGrain
    : ActivationCancellationTestGrainBase, IActivationCancellation_ThrowsObjectDisposedGrain
{
    public ActivationCancellation_ThrowsObjectDisposedGrain(
        ILogger<ActivationCancellation_ThrowsObjectDisposedGrain> logger,
        IGrainRuntime grainRuntime)
        : base(logger, grainRuntime)
    {
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        Logger.LogDebug("OnActivateAsync starting for {GrainType}", GetType().Name);

        if (RequestContext.Get("delay_activation_ms") is int delayMs && delayMs > 0)
        {
            Logger.LogDebug("Setting up cancellation callback to throw ObjectDisposedException after {DelayMs}ms max", delayMs);
            
            var tcs = new TaskCompletionSource<bool>();
            
            // Register callback to throw ObjectDisposedException when cancellation is requested
            await using var registration = cancellationToken.Register(() =>
            {
                Logger.LogDebug("Cancellation was requested, throwing ObjectDisposedException");
                tcs.TrySetException(new ObjectDisposedException("IServiceProvider", "The service provider has been disposed because the activation was cancelled."));
            });

            Thread.Sleep(TimeSpan.FromMilliseconds(delayMs));

            await tcs.Task;
        }

        IsActivatedSuccessfully = true;
        Logger.LogDebug("OnActivateAsync completed successfully for {GrainType}", GetType().Name);
        await base.OnActivateAsync(cancellationToken);
    }
}

/// <summary>
/// Grain that throws a generic exception during OnActivateAsync (not related to cancellation).
/// </summary>
public class ActivationCancellation_ThrowsGenericExceptionGrain
    : ActivationCancellationTestGrainBase, IActivationCancellation_ThrowsGenericExceptionGrain
{
    public ActivationCancellation_ThrowsGenericExceptionGrain(ILogger<ActivationCancellation_ThrowsGenericExceptionGrain> logger, IGrainRuntime grainRuntime)
        : base(logger, grainRuntime)
    {
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        Logger.LogDebug("OnActivateAsync starting for {GrainType}", GetType().Name);

        // Check if we should throw an exception
        if (RequestContext.Get("throw_exception") is bool shouldThrow && shouldThrow)
        {
            Logger.LogDebug("Throwing generic exception as requested");
            throw new InvalidOperationException("This is a test exception thrown during activation.");
        }

        IsActivatedSuccessfully = true;
        Logger.LogDebug("OnActivateAsync completed successfully for {GrainType}", GetType().Name);
        return base.OnActivateAsync(cancellationToken);
    }
}

/// <summary>
/// Grain that activates successfully without any issues.
/// </summary>
public class ActivationCancellation_SuccessfulActivationGrain
    : ActivationCancellationTestGrainBase, IActivationCancellation_SuccessfulActivationGrain
{
    public ActivationCancellation_SuccessfulActivationGrain(ILogger<ActivationCancellation_SuccessfulActivationGrain> logger, IGrainRuntime grainRuntime)
        : base(logger, grainRuntime)
    {
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        Logger.LogDebug("OnActivateAsync starting for {GrainType}", GetType().Name);
        IsActivatedSuccessfully = true;
        Logger.LogDebug("OnActivateAsync completed successfully for {GrainType}", GetType().Name);
        return base.OnActivateAsync(cancellationToken);
    }
}

/// <summary>
/// Grain that throws TaskCanceledException during OnActivateAsync.
/// TaskCanceledException inherits from OperationCanceledException and should be handled the same way.
/// </summary>
public class ActivationCancellation_ThrowsTaskCancelledGrain
    : ActivationCancellationTestGrainBase, IActivationCancellation_ThrowsTaskCancelledGrain
{
    public ActivationCancellation_ThrowsTaskCancelledGrain(ILogger<ActivationCancellation_ThrowsTaskCancelledGrain> logger, IGrainRuntime grainRuntime)
        : base(logger, grainRuntime)
    {
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        Logger.LogDebug("OnActivateAsync starting for {GrainType}", GetType().Name);

        if (RequestContext.Get("delay_activation_ms") is int delayMs && delayMs > 0)
        {
            Logger.LogDebug("Setting up cancellation callback to throw TaskCanceledException after {DelayMs}ms max", delayMs);
            
            var tcs = new TaskCompletionSource<bool>();
            
            // Register callback to throw TaskCanceledException when cancellation is requested
            /*await using var registration = cancellationToken.Register(() =>
            {
                Logger.LogDebug("Cancellation was requested, throwing TaskCanceledException");
                tcs.TrySetException(new TaskCanceledException("Activation was cancelled", null, cancellationToken));
            });*/

            // Start the delay task (without cancellation token)
            var delayTask = Task.Delay(TimeSpan.FromMilliseconds(delayMs), TimeProvider, CancellationToken.None);
            
            // Wait for either the delay to complete or the cancellation to trigger the exception
            var completedTask = await Task.WhenAny(delayTask, tcs.Task);
            
            // If the TCS task completed, it has an exception - await it to propagate
            if (completedTask == tcs.Task)
            {
                await tcs.Task;
            }
        }

        IsActivatedSuccessfully = true;
        Logger.LogDebug("OnActivateAsync completed successfully for {GrainType}", GetType().Name);
        await base.OnActivateAsync(cancellationToken);
    }
}

/// <summary>
/// Grain that throws ObjectDisposedException unconditionally (not due to cancellation).
/// This tests that ObjectDisposedException thrown for other reasons is NOT treated as cancellation.
/// </summary>
public class ActivationCancellation_ThrowsObjectDisposedUnconditionallyGrain
    : ActivationCancellationTestGrainBase, IActivationCancellation_ThrowsObjectDisposedUnconditionallyGrain
{
    public ActivationCancellation_ThrowsObjectDisposedUnconditionallyGrain(
        ILogger<ActivationCancellation_ThrowsObjectDisposedUnconditionallyGrain> logger,
        IGrainRuntime grainRuntime)
        : base(logger, grainRuntime)
    {
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        Logger.LogDebug("OnActivateAsync starting for {GrainType}", GetType().Name);

        // Check if we should throw an exception (without cancellation being requested)
        if (RequestContext.Get("throw_object_disposed") is bool shouldThrow && shouldThrow)
        {
            Logger.LogDebug("Throwing ObjectDisposedException unconditionally (cancellation NOT requested)");
            // This simulates a bug where ObjectDisposedException is thrown for reasons
            // unrelated to cancellation - should NOT be treated as ActivationCancelledException
            throw new ObjectDisposedException("SomeObject", "This object was disposed for reasons unrelated to cancellation.");
        }

        IsActivatedSuccessfully = true;
        Logger.LogDebug("OnActivateAsync completed successfully for {GrainType}", GetType().Name);
        return base.OnActivateAsync(cancellationToken);
    }
}

/// <summary>
/// Grain that throws OperationCanceledException unconditionally (not due to cancellation token being cancelled).
/// This tests that OperationCanceledException thrown for other reasons is NOT treated as ActivationCancelledException.
/// </summary>
public class ActivationCancellation_ThrowsOperationCancelledUnconditionallyGrain
    : ActivationCancellationTestGrainBase, IActivationCancellation_ThrowsOperationCancelledUnconditionallyGrain
{
    public ActivationCancellation_ThrowsOperationCancelledUnconditionallyGrain(
        ILogger<ActivationCancellation_ThrowsOperationCancelledUnconditionallyGrain> logger,
        IGrainRuntime grainRuntime)
        : base(logger, grainRuntime)
    {
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        Logger.LogDebug("OnActivateAsync starting for {GrainType}", GetType().Name);

        // Check if we should throw an exception (without cancellation being requested)
        if (RequestContext.Get("throw_operation_cancelled") is bool shouldThrow && shouldThrow)
        {
            Logger.LogDebug("Throwing OperationCanceledException unconditionally (cancellation NOT requested on the passed token)");
            // This simulates a scenario where OperationCanceledException is thrown for reasons
            // unrelated to the activation's cancellation token being cancelled
            // The code should check cancellationToken.IsCancellationRequested before converting to ActivationCancelledException
            throw new OperationCanceledException("Operation was cancelled for reasons unrelated to activation cancellation.");
        }

        IsActivatedSuccessfully = true;
        Logger.LogDebug("OnActivateAsync completed successfully for {GrainType}", GetType().Name);
        return base.OnActivateAsync(cancellationToken);
    }
}
