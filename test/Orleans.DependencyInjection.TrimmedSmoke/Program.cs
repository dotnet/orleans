using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Hosting;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Runtime.Placement;

namespace Orleans.DependencyInjection.TrimmedSmoke;

internal static class Program
{
    private const int StartupStage = 4711;

    private static async Task Main()
    {
        ValidateCoreRegistration();
        ValidateRuntimeRegistration();
        await ValidateStartupTaskActivation();

        Console.WriteLine("Selected generic DI constructor flows survived the self-contained trimmed smoke.");
    }

    private static void ValidateCoreRegistration()
    {
        var marker = new ActivationMarker();
        var services = new ServiceCollection();
        services.AddSingleton(marker);
        services.AddPlacementFilter<SmokePlacementFilterStrategy, SmokePlacementFilterDirector>(
            ServiceLifetime.Singleton);

        using var provider = services.BuildServiceProvider();
        var strategy = provider.GetRequiredKeyedService<PlacementFilterStrategy>(
            nameof(SmokePlacementFilterStrategy));
        var director = provider.GetRequiredKeyedService<IPlacementFilterDirector>(
            typeof(SmokePlacementFilterStrategy));

        Ensure(
            strategy.GetType() == typeof(SmokePlacementFilterStrategy),
            "The Core generic registration did not resolve the expected keyed strategy.");
        Ensure(
            director.GetType() == typeof(SmokePlacementFilterDirector),
            "The Core generic registration did not construct the expected keyed director.");
        Ensure(
            ReferenceEquals(((SmokePlacementFilterDirector)director).Marker, marker),
            "The Core generic registration did not preserve constructor dependency identity.");
    }

    private static void ValidateRuntimeRegistration()
    {
        var marker = new ActivationMarker();
        var services = new ServiceCollection();
        services.AddSingleton(marker);
        services.AddPlacementDirector<SmokePlacementStrategy, SmokePlacementDirector>();

        using var provider = services.BuildServiceProvider();
        var strategy = provider.GetRequiredKeyedService<PlacementStrategy>(
            nameof(SmokePlacementStrategy));
        var director = provider.GetRequiredKeyedService<IPlacementDirector>(
            typeof(SmokePlacementStrategy));

        Ensure(
            strategy.GetType() == typeof(SmokePlacementStrategy),
            "The Runtime generic registration did not resolve the expected keyed strategy.");
        Ensure(
            director.GetType() == typeof(SmokePlacementDirector),
            "The Runtime generic registration did not construct the expected keyed director.");
        Ensure(
            ReferenceEquals(((SmokePlacementDirector)director).Marker, marker),
            "The Runtime generic registration did not preserve constructor dependency identity.");
    }

    private static async Task ValidateStartupTaskActivation()
    {
        var marker = new ActivationMarker();
        var recorder = new StartupTaskRecorder();
        var builder = new SmokeSiloBuilder();
        builder.Services.AddSingleton(marker);
        builder.Services.AddSingleton(recorder);
        builder.AddStartupTask<SmokeStartupTask>(StartupStage);

        using var provider = builder.Services.BuildServiceProvider();
        var participant = provider.GetRequiredService<ILifecycleParticipant<ISiloLifecycle>>();
        var lifecycle = new RecordingSiloLifecycle();
        participant.Participate(lifecycle);

        Ensure(lifecycle.Stage == StartupStage, "The startup task was not registered at the expected lifecycle stage.");
        Ensure(recorder.ConstructedTask is null, "The startup task was constructed before lifecycle execution.");
        Ensure(recorder.ExecutedTask is null, "The startup task executed before the lifecycle callback.");

        await lifecycle.StartAsync(CancellationToken.None);

        Ensure(
            recorder.ConstructedTask is SmokeStartupTask,
            "The startup task was not constructed by the lifecycle callback.");
        Ensure(
            ReferenceEquals(recorder.ConstructedTask.Marker, marker),
            "The startup task did not preserve constructor dependency identity.");
        Ensure(
            ReferenceEquals(recorder.ExecutedTask, recorder.ConstructedTask),
            "The constructor-injected startup task was not executed.");
    }

    private static void Ensure([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

internal sealed class ActivationMarker;

internal sealed class SmokePlacementFilterStrategy : PlacementFilterStrategy
{
    public SmokePlacementFilterStrategy()
        : base(17)
    {
    }
}

internal sealed class SmokePlacementFilterDirector : IPlacementFilterDirector
{
    public SmokePlacementFilterDirector(ActivationMarker marker)
    {
        Marker = marker;
    }

    public ActivationMarker Marker { get; }

    public IEnumerable<SiloAddress> Filter(
        PlacementFilterStrategy filterStrategy,
        PlacementTarget target,
        IEnumerable<SiloAddress> silos) => silos;
}

internal sealed class SmokePlacementStrategy : PlacementStrategy
{
    public SmokePlacementStrategy()
    {
    }
}

internal sealed class SmokePlacementDirector : IPlacementDirector
{
    public SmokePlacementDirector(ActivationMarker marker)
    {
        Marker = marker;
    }

    public ActivationMarker Marker { get; }

    public Task<SiloAddress> OnAddActivation(
        PlacementStrategy strategy,
        PlacementTarget target,
        IPlacementContext context) => throw new NotSupportedException();
}

internal sealed class SmokeSiloBuilder : ISiloBuilder
{
    public IServiceCollection Services { get; } = new ServiceCollection();

    public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
}

internal sealed class SmokeStartupTask : IStartupTask
{
    private readonly StartupTaskRecorder _recorder;

    public SmokeStartupTask(ActivationMarker marker, StartupTaskRecorder recorder)
    {
        Marker = marker;
        _recorder = recorder;
        recorder.ConstructedTask = this;
    }

    public ActivationMarker Marker { get; }

    public Task Execute(CancellationToken cancellationToken)
    {
        _recorder.ExecutedTask = this;
        return Task.CompletedTask;
    }
}

internal sealed class StartupTaskRecorder
{
    public SmokeStartupTask? ConstructedTask { get; set; }

    public SmokeStartupTask? ExecutedTask { get; set; }
}

internal sealed class RecordingSiloLifecycle : ISiloLifecycle
{
    private ILifecycleObserver? _observer;

    public int HighestCompletedStage => 0;

    public int LowestStoppedStage => 0;

    public int? Stage { get; private set; }

    public IDisposable Subscribe(string observerName, int stage, ILifecycleObserver observer)
    {
        Ensure(_observer is null, "The recording lifecycle received more than one observer.");
        Stage = stage;
        _observer = observer;
        return NoopDisposable.Instance;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Ensure(_observer is not null, "The startup lifecycle observer was not registered.");
        return _observer.OnStart(cancellationToken);
    }

    private static void Ensure([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

internal sealed class NoopDisposable : IDisposable
{
    public static NoopDisposable Instance { get; } = new();

    public void Dispose()
    {
    }
}
