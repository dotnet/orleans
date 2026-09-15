using Orleans.Concurrency;
using Orleans.Runtime;

namespace UnitTests.Grains;

public interface IStatelessWorkerActivationStartupTestGrain : IGrainWithStringKey
{
    Task<string> Invoke(string payload);
}

[StatelessWorker(1)]
public sealed class StatelessWorkerActivationStartupTestGrain :
    IStatelessWorkerActivationStartupTestGrain,
    IGrainBase,
    ILifecycleParticipant<IGrainLifecycle>
{
    private readonly ActivationStartupScenario _scenario;

    public StatelessWorkerActivationStartupTestGrain(
        IGrainContext grainContext,
        ActivationStartupTestHooks hooks)
    {
        GrainContext = grainContext;
        _scenario = hooks.GetRequiredScenario(grainContext.GrainId);
        _scenario.ObserveConstructor(grainContext);
        _scenario.Record("ConstructorEntered", grainContext);
    }

    public IGrainContext GrainContext { get; }

    public void Participate(IGrainLifecycle lifecycle)
    {
        lifecycle.Subscribe(
            "StatelessWorkerActivationStartup-Low",
            GrainLifecycleStage.SetupState,
            _ =>
            {
                _scenario.Record("LifecycleStartLow", GrainContext);
                return Task.CompletedTask;
            },
            _ =>
            {
                _scenario.Record("LifecycleStopLow", GrainContext);
                return Task.CompletedTask;
            });
        lifecycle.Subscribe(
            "StatelessWorkerActivationStartup-High",
            GrainLifecycleStage.Activate,
            _ =>
            {
                _scenario.Record("LifecycleStartHigh", GrainContext);
                return Task.CompletedTask;
            },
            _ =>
            {
                _scenario.Record("LifecycleStopHigh", GrainContext);
                return Task.CompletedTask;
            });
    }

    public Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _scenario.ObserveOnActivate(GrainContext);
        return _scenario.Completion switch
        {
            ActivationStartupCompletion.ImmediateSuccess => CompleteActivation(),
            ActivationStartupCompletion.ImmediateFailure => FailActivation(),
            ActivationStartupCompletion.AsynchronousSuccess => CompleteActivationAfterRelease(cancellationToken),
            ActivationStartupCompletion.AsynchronousFailure => FailActivationAfterRelease(cancellationToken),
            ActivationStartupCompletion.Cancellation => CompleteActivationAfterRelease(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(),
        };
    }

    public Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        _scenario.ObserveOnDeactivate(GrainContext);
        _scenario.Record("OnDeactivateCompleted", GrainContext);
        return Task.CompletedTask;
    }

    public Task<string> Invoke(string payload)
    {
        _scenario.ObserveRequest(GrainContext);
        return Task.FromResult(
            $"{payload}:{GrainContext.ActivationId}:{_scenario.RequestContextValue}");
    }

    private Task CompleteActivation()
    {
        _scenario.Record("OnActivateCompleted", GrainContext);
        return Task.CompletedTask;
    }

    private Task FailActivation()
    {
        _scenario.Record("OnActivateFailed", GrainContext);
        throw _scenario.ActivationException;
    }

    private async Task CompleteActivationAfterRelease(CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(
            static state =>
            {
                var (scenario, context) = ((ActivationStartupScenario, IGrainContext))state!;
                scenario.Record("CancellationObserved", context);
            },
            (_scenario, GrainContext));
        await _scenario.ActivationRelease.WaitAsync(cancellationToken);
        _scenario.Record("OnActivateCompleted", GrainContext);
    }

    private async Task FailActivationAfterRelease(CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(
            static state =>
            {
                var (scenario, context) = ((ActivationStartupScenario, IGrainContext))state!;
                scenario.Record("CancellationObserved", context);
            },
            (_scenario, GrainContext));
        await _scenario.ActivationRelease.WaitAsync(cancellationToken);
        _scenario.Record("OnActivateFailed", GrainContext);
        throw _scenario.ActivationException;
    }
}
