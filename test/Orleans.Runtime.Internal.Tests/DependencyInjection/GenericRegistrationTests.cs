using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Hosting;
using Orleans.Placement.Rebalancing;
using Orleans.Placement.Repartitioning;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
using Orleans.Runtime.Utilities;
using TestExtensions;
using Xunit;

namespace UnitTests.DependencyInjection;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
[TestCategory("BVT")]
public class RuntimeGenericRegistrationTests
{
    [Fact]
    public void FactoryUtility_Create_ActivatesConstructorsForAllSupportedArities()
    {
        var dependency = new ActivationMarker();
        var argument1 = new FactoryArgument1();
        var argument2 = new FactoryArgument2();
        var argument3 = new FactoryArgument3();
        var services = new ServiceCollection();
        services.AddSingleton(dependency);

        using var provider = services.BuildServiceProvider();
        var arityZero = FactoryUtility.Create<FactoryProbe0>(provider)();
        var arityOne = FactoryUtility.Create<FactoryArgument1, FactoryProbe1>(provider)(argument1);
        var arityTwo = FactoryUtility.Create<FactoryArgument1, FactoryArgument2, FactoryProbe2>(provider)(
            argument1,
            argument2);
        var arityThree = FactoryUtility.Create<
            FactoryArgument1,
            FactoryArgument2,
            FactoryArgument3,
            FactoryProbe3>(provider)(argument1, argument2, argument3);

        Assert.IsType<FactoryProbe0>(arityZero);
        Assert.Same(dependency, arityZero.Dependency);

        Assert.IsType<FactoryProbe1>(arityOne);
        Assert.Same(dependency, arityOne.Dependency);
        Assert.Same(argument1, arityOne.Argument1);

        Assert.IsType<FactoryProbe2>(arityTwo);
        Assert.Same(dependency, arityTwo.Dependency);
        Assert.Same(argument1, arityTwo.Argument1);
        Assert.Same(argument2, arityTwo.Argument2);

        Assert.IsType<FactoryProbe3>(arityThree);
        Assert.Same(dependency, arityThree.Dependency);
        Assert.Same(argument1, arityThree.Argument1);
        Assert.Same(argument2, arityThree.Argument2);
        Assert.Same(argument3, arityThree.Argument3);
    }

    [Fact]
    public void PlacementDirector_ActivatesConstructorInjectedDirector()
    {
        var dependency = new ActivationMarker();
        var services = new ServiceCollection();
        services.AddSingleton(dependency);
        services.AddPlacementDirector<TestPlacementStrategy, ConstructorInjectedPlacementDirector>();

        using var provider = services.BuildServiceProvider();
        var strategy = Assert.IsType<TestPlacementStrategy>(
            provider.GetRequiredKeyedService<PlacementStrategy>(nameof(TestPlacementStrategy)));
        var director = Assert.IsType<ConstructorInjectedPlacementDirector>(
            provider.GetRequiredKeyedService<IPlacementDirector>(typeof(TestPlacementStrategy)));

        Assert.True(strategy.IsUsingGrainDirectory);
        Assert.Same(dependency, director.Dependency);
    }

    [Fact]
    public void ActivationRebalancer_ActivatesConstructorInjectedBackoffProvider()
    {
        var dependency = new ActivationMarker();
        var builder = new TestSiloBuilder();
        builder.Services.AddSingleton(dependency);
#pragma warning disable ORLEANSEXP002
        builder.AddActivationRebalancer<ConstructorInjectedBackoffProvider>();
#pragma warning restore ORLEANSEXP002

        using var provider = builder.Services.BuildServiceProvider();
        var concrete = provider.GetRequiredService<ConstructorInjectedBackoffProvider>();
        var alias = provider.GetRequiredService<IFailedSessionBackoffProvider>();

        Assert.Same(concrete, alias);
        Assert.Same(dependency, concrete.Dependency);
    }

    [Fact]
    public void ActivationRepartitioner_ActivatesConstructorInjectedToleranceRule()
    {
        var dependency = new ActivationMarker();
        var builder = new TestSiloBuilder();
        builder.Services.AddSingleton(dependency);
#pragma warning disable ORLEANSEXP001
        builder.AddActivationRepartitioner<ConstructorInjectedToleranceRule>();
#pragma warning restore ORLEANSEXP001

        using var provider = builder.Services.BuildServiceProvider();
        var concrete = provider.GetRequiredService<ConstructorInjectedToleranceRule>();
        var alias = provider.GetRequiredService<IImbalanceToleranceRule>();

        Assert.Same(concrete, alias);
        Assert.Same(dependency, concrete.Dependency);
    }

    [Fact]
    public void GrainExtension_ActivatesConstructorInjectedImplementation()
    {
        var dependency = new ActivationMarker();
        var builder = new TestSiloBuilder();
        builder.Services.AddSingleton(dependency);
        builder.AddGrainExtension<ITestGrainExtension, ConstructorInjectedGrainExtension>();

        using var provider = builder.Services.BuildServiceProvider();
        var extension = Assert.IsType<ConstructorInjectedGrainExtension>(
            provider.GetRequiredKeyedService<IGrainExtension>(typeof(ITestGrainExtension)));

        Assert.Same(dependency, extension.Dependency);
    }

    [Fact]
    public async Task StartupTask_ActivatesConstructorInjectedTask()
    {
        const int startupStage = 4711;
        var dependency = new ActivationMarker();
        var recorder = new StartupTaskRecorder();
        var builder = new TestSiloBuilder();
        builder.Services.AddSingleton(dependency);
        builder.Services.AddSingleton(recorder);
        builder.AddStartupTask<ConstructorInjectedStartupTask>(startupStage);

        using var provider = builder.Services.BuildServiceProvider();
        var participant = provider.GetRequiredService<ILifecycleParticipant<ISiloLifecycle>>();
        var lifecycle = new RecordingSiloLifecycle();
        participant.Participate(lifecycle);

        Assert.Equal(startupStage, lifecycle.Stage);
        Assert.Null(recorder.ConstructedTask);
        Assert.Null(recorder.ExecutedTask);

        await lifecycle.StartAsync(CancellationToken.None);

        var task = Assert.IsType<ConstructorInjectedStartupTask>(recorder.ConstructedTask);
        Assert.Same(dependency, task.Dependency);
        Assert.Same(task, recorder.ExecutedTask);
    }

    private sealed class ActivationMarker;

    private sealed class FactoryArgument1;

    private sealed class FactoryArgument2;

    private sealed class FactoryArgument3;

    private sealed class FactoryProbe0
    {
        public FactoryProbe0(ActivationMarker dependency)
        {
            Dependency = dependency;
        }

        public ActivationMarker Dependency { get; }
    }

    private sealed class FactoryProbe1
    {
        public FactoryProbe1(ActivationMarker dependency, FactoryArgument1 argument1)
        {
            Dependency = dependency;
            Argument1 = argument1;
        }

        public ActivationMarker Dependency { get; }

        public FactoryArgument1 Argument1 { get; }
    }

    private sealed class FactoryProbe2
    {
        public FactoryProbe2(
            FactoryArgument1 argument1,
            ActivationMarker dependency,
            FactoryArgument2 argument2)
        {
            Dependency = dependency;
            Argument1 = argument1;
            Argument2 = argument2;
        }

        public ActivationMarker Dependency { get; }

        public FactoryArgument1 Argument1 { get; }

        public FactoryArgument2 Argument2 { get; }
    }

    private sealed class FactoryProbe3
    {
        public FactoryProbe3(
            FactoryArgument1 argument1,
            FactoryArgument2 argument2,
            ActivationMarker dependency,
            FactoryArgument3 argument3)
        {
            Dependency = dependency;
            Argument1 = argument1;
            Argument2 = argument2;
            Argument3 = argument3;
        }

        public ActivationMarker Dependency { get; }

        public FactoryArgument1 Argument1 { get; }

        public FactoryArgument2 Argument2 { get; }

        public FactoryArgument3 Argument3 { get; }
    }

    private sealed class TestPlacementStrategy : PlacementStrategy;

    private sealed class ConstructorInjectedPlacementDirector : IPlacementDirector
    {
        public ConstructorInjectedPlacementDirector(ActivationMarker dependency)
        {
            Dependency = dependency;
        }

        public ActivationMarker Dependency { get; }

        public Task<SiloAddress> OnAddActivation(
            PlacementStrategy strategy,
            PlacementTarget target,
            IPlacementContext context) => throw new NotSupportedException();
    }

    private sealed class ConstructorInjectedBackoffProvider : IFailedSessionBackoffProvider
    {
        public ConstructorInjectedBackoffProvider(ActivationMarker dependency)
        {
            Dependency = dependency;
        }

        public ActivationMarker Dependency { get; }

        public TimeSpan Next(int attempt) => TimeSpan.Zero;
    }

    private sealed class ConstructorInjectedToleranceRule : IImbalanceToleranceRule
    {
        public ConstructorInjectedToleranceRule(ActivationMarker dependency)
        {
            Dependency = dependency;
        }

        public ActivationMarker Dependency { get; }

        public bool IsSatisfiedBy(uint imbalance) => true;
    }

    public interface ITestGrainExtension : IGrainExtension;

    private sealed class ConstructorInjectedGrainExtension : ITestGrainExtension
    {
        public ConstructorInjectedGrainExtension(ActivationMarker dependency)
        {
            Dependency = dependency;
        }

        public ActivationMarker Dependency { get; }
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }

    private sealed class ConstructorInjectedStartupTask : IStartupTask
    {
        private readonly StartupTaskRecorder _recorder;

        public ConstructorInjectedStartupTask(ActivationMarker dependency, StartupTaskRecorder recorder)
        {
            Dependency = dependency;
            _recorder = recorder;
            recorder.ConstructedTask = this;
        }

        public ActivationMarker Dependency { get; }

        public Task Execute(CancellationToken cancellationToken)
        {
            _recorder.ExecutedTask = this;
            return Task.CompletedTask;
        }
    }

    private sealed class StartupTaskRecorder
    {
        public ConstructorInjectedStartupTask? ConstructedTask { get; set; }

        public ConstructorInjectedStartupTask? ExecutedTask { get; set; }
    }

    private sealed class RecordingSiloLifecycle : ISiloLifecycle
    {
        private ILifecycleObserver? _observer;

        public int HighestCompletedStage => 0;

        public int LowestStoppedStage => 0;

        public int? Stage { get; private set; }

        public IDisposable Subscribe(string observerName, int stage, ILifecycleObserver observer)
        {
            Assert.Null(_observer);
            Stage = stage;
            _observer = observer;
            return NoopDisposable.Instance;
        }

        public Task StartAsync(CancellationToken cancellationToken) =>
            Assert.IsAssignableFrom<ILifecycleObserver>(_observer).OnStart(cancellationToken);
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
