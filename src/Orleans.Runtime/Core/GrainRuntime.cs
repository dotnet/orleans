#nullable enable
using System;
using Orleans.Core;
using Orleans.Timers;
using Orleans.Storage;

namespace Orleans.Runtime;

internal class GrainRuntime : IGrainRuntime
{
    private readonly IServiceProvider _serviceProvider;

    private readonly ITimerRegistry _timerRegistry;
    private readonly IGrainFactory _grainFactory;

    public GrainRuntime(
        ILocalSiloDetails localSiloDetails,
        IGrainFactory grainFactory,
        ITimerRegistry timerRegistry,
        IServiceProvider serviceProvider,
        TimeProvider timeProvider)
    {
        SiloAddress = localSiloDetails.SiloAddress;
        SiloIdentity = SiloAddress.ToString();
        _grainFactory = grainFactory;
        _timerRegistry = timerRegistry;
        _serviceProvider = serviceProvider;
        TimeProvider = timeProvider;
    }

    public string SiloIdentity { get; }

    public SiloAddress SiloAddress { get; }

    public IGrainFactory GrainFactory
    {
        get
        {
            CheckRuntimeContext(RuntimeContext.Current);
            return _grainFactory;
        }
    }

    public ITimerRegistry TimerRegistry
    {
        get
        {
            CheckRuntimeContext(RuntimeContext.Current);
            return _timerRegistry;
        }
    }

    public IServiceProvider ServiceProvider
    {
        get
        {
            CheckRuntimeContext(RuntimeContext.Current);
            return _serviceProvider;
        }
    }

    public TimeProvider TimeProvider { get; }

    public void DeactivateOnIdle(IGrainContext grainContext)
    {
        CheckRuntimeContext(grainContext);
        grainContext.Deactivate(new(DeactivationReasonCode.ApplicationRequested, $"{nameof(DeactivateOnIdle)} was called."));
    }

    public void DelayDeactivation(IGrainContext grainContext, TimeSpan timeSpan)
    {
        CheckRuntimeContext(grainContext);
        if (grainContext is not ICollectibleGrainContext collectibleContext)
        {
            throw new NotSupportedException($"Grain context {grainContext} does not implement {nameof(ICollectibleGrainContext)} and therefore {nameof(DelayDeactivation)} is not supported");
        }

        collectibleContext.DelayDeactivation(timeSpan);
    }

    public IStorage<TGrainState> GetStorage<TGrainState>(IGrainContext grainContext)
    {
        ArgumentNullException.ThrowIfNull(grainContext);
        var grainType = grainContext.GrainInstance?.GetType() ?? throw new ArgumentNullException(nameof(IGrainContext.GrainInstance));
        IGrainStorage grainStorage = GrainStorageHelpers.GetGrainStorage(grainType, ServiceProvider);
        return new StateStorageBridge<TGrainState>("state", grainContext, grainStorage);
    }

    public static void CheckRuntimeContext(IGrainContext? context)
    {
        if (context is null)
        {
            // Move exceptions into local functions to help inlining this method.
            ThrowMissingContext();
            void ThrowMissingContext() => throw new InvalidOperationException("Activation access violation. A non-activation thread attempted to access activation services.");
        }

        if (context is ActivationData activation && activation.State == ActivationState.Invalid)
        {
            // Move exceptions into local functions to help inlining this method.
            ThrowInvalidActivation(activation);
            void ThrowInvalidActivation(ActivationData activationData) => throw new InvalidOperationException($"Attempt to access an invalid activation: {activationData}");
        }
    }
}
