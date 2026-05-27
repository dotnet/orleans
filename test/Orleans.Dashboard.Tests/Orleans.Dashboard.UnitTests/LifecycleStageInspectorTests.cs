using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans;
using Orleans.Dashboard.Implementation;
using Orleans.Runtime;
using Xunit;

namespace UnitTests;

public class LifecycleStageInspectorTests
{
    [Fact]
    public void GetStages_GroupsObserversByStage_AndPreservesOrder()
    {
        var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);

        lifecycle.Subscribe("A", ServiceLifecycleStage.RuntimeInitialize, Start, Stop);
        lifecycle.Subscribe("B", ServiceLifecycleStage.RuntimeServices, Start, Stop);
        lifecycle.Subscribe("C", ServiceLifecycleStage.RuntimeServices, Start, Stop);
        lifecycle.Subscribe("D", 4242, Start, Stop); // numeric-only stage

        var stages = LifecycleStageInspector.GetStages(lifecycle);

        // 3 distinct stages, ordered ascending
        Assert.Equal(3, stages.Length);
        Assert.Equal(ServiceLifecycleStage.RuntimeInitialize, stages[0].Stage);
        Assert.Equal(ServiceLifecycleStage.RuntimeServices, stages[1].Stage);
        Assert.Equal(4242, stages[2].Stage);

        // Named stages flagged as such; numeric-only stages are not.
        Assert.True(stages[0].IsNamedStage);
        Assert.True(stages[1].IsNamedStage);
        Assert.False(stages[2].IsNamedStage);

        // Observers grouped per stage with their registration names.
        Assert.Single(stages[0].Observers);
        Assert.Equal("A", stages[0].Observers[0].Name);
        Assert.Equal(2, stages[1].Observers.Length);
        Assert.Equal(["B", "C"], stages[1].Observers.Select(o => o.Name).ToArray());
        Assert.Single(stages[2].Observers);
        Assert.Equal("D", stages[2].Observers[0].Name);

        // Delegate-based subscribers expose method names for OnStart/OnStop.
        var aObserver = stages[0].Observers[0];
        Assert.True(aObserver.HasOnStart);
        Assert.True(aObserver.HasOnStop);
        Assert.Contains("Start", aObserver.OnStartMethod, System.StringComparison.Ordinal);
        Assert.Contains("Stop", aObserver.OnStopMethod, System.StringComparison.Ordinal);
    }

    [Fact]
    public void GetStages_StartupOnlyObserver_IsRecognisedAsStartOnly()
    {
        var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);

        // Subscribe overload without onStop creates a StartupObserver.
        lifecycle.Subscribe("StartOnly", ServiceLifecycleStage.ApplicationServices, Start);

        var stages = LifecycleStageInspector.GetStages(lifecycle);
        var observer = Assert.Single(Assert.Single(stages).Observers);

        Assert.True(observer.HasOnStart);
        Assert.False(observer.HasOnStop);
        Assert.NotNull(observer.OnStartMethod);
        Assert.Null(observer.OnStopMethod);
    }

    [Fact]
    public void GetStages_HandlesNullLifecycle()
    {
        var stages = LifecycleStageInspector.GetStages(null);
        Assert.Empty(stages);
    }

    [Fact]
    public void GetStages_PrettyPrintsStaticLocalFunctionNames_EvenInExplicitInterfaceImplementations()
    {
        // A type that subscribes via Orleans.ILifecycleParticipant<ISiloLifecycle>.Participate
        // (explicit interface). This exercises the nested-generic outer-name path
        // in LifecycleStageInspector.CleanMethodName.
        var subject = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        var participant = new ExplicitInterfaceParticipant();
        ((ILifecycleParticipant<ISiloLifecycle>)participant).Participate(subject);

        var stages = LifecycleStageInspector.GetStages(subject);
        var observer = Assert.Single(Assert.Single(stages).Observers);

        Assert.Equal("MyStaticLocalStart", observer.OnStartMethod?.Split('.')[^1]);
        Assert.Equal("MyStaticLocalStop", observer.OnStopMethod?.Split('.')[^1]);
        Assert.DoesNotContain("g__", observer.OnStartMethod ?? string.Empty, System.StringComparison.Ordinal);
        Assert.DoesNotContain("g__", observer.OnStopMethod ?? string.Empty, System.StringComparison.Ordinal);
    }

    [Fact]
    public void GetStages_LabelsAnonymousLambdas_AsLambdaInOuterMethod()
    {
        var subject = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        subject.Subscribe("WithLambda", ServiceLifecycleStage.ApplicationServices, _ => Task.CompletedTask, _ => Task.CompletedTask);

        var stages = LifecycleStageInspector.GetStages(subject);
        var observer = Assert.Single(Assert.Single(stages).Observers);

        // Anonymous lambdas inside this test method should be reported as
        // `<lambda in GetStages_LabelsAnonymousLambdas_AsLambdaInOuterMethod>` rather than
        // exposing the compiler-generated b__N_N suffix.
        Assert.Contains("<lambda in", observer.OnStartMethod ?? string.Empty, System.StringComparison.Ordinal);
        Assert.Contains("<lambda in", observer.OnStopMethod ?? string.Empty, System.StringComparison.Ordinal);
        Assert.DoesNotContain("b__", observer.OnStartMethod ?? string.Empty, System.StringComparison.Ordinal);
    }

    private static Task Start(CancellationToken ct) => Task.CompletedTask;

    private static Task Stop(CancellationToken ct) => Task.CompletedTask;

    private sealed class ExplicitInterfaceParticipant : ILifecycleParticipant<ISiloLifecycle>
    {
        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle observer)
        {
            observer.Subscribe(nameof(ExplicitInterfaceParticipant), ServiceLifecycleStage.RuntimeServices, MyStaticLocalStart, MyStaticLocalStop);

            static Task MyStaticLocalStart(CancellationToken _) => Task.CompletedTask;
            static Task MyStaticLocalStop(CancellationToken _) => Task.CompletedTask;
        }
    }
}
