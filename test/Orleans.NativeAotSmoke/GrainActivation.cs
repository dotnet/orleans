using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.TypeSystem;

namespace Orleans.NativeAotSmoke;

internal static class GrainActivation
{
    private static async Task Main()
    {
        var manifest = new TypeManifestOptions();
        ITypeManifestProvider generated = new OrleansCodeGen.OrleansNativeAotSmoke.Metadata_OrleansNativeAotSmoke();
        generated.Configure(manifest);
        var converter = new TypeConverter([], [], [], Options.Create(manifest), new CachedTypeResolver());
        var map = new GrainClassMap(converter, GetRegisteredGrainClasses(manifest));

        using var services = new ServiceCollection()
            .AddScoped<GrainActivationDependency>(_ => new GrainActivationDependency())
            .BuildServiceProvider();
        using var firstScope = services.CreateScope();
        using var secondScope = services.CreateScope();
        var firstContext = new GrainActivationContext(firstScope.ServiceProvider);
        var secondContext = new GrainActivationContext(secondScope.ServiceProvider);

        var parameterless = GetActivator(nameof(GrainActivationParameterlessGrain), services, map);
        var addition = (GrainActivationParameterlessGrain)parameterless.CreateInstance(firstContext);
        Require(await addition.Add(17, 25) == 42, "The implicit public parameterless constructor activates a working grain.");

        var preferred = GetActivator(nameof(GrainActivationPreferredGrain), services, map);
        var preferredGrain = (GrainActivationPreferredGrain)preferred.CreateInstance(firstContext);
        Require(preferredGrain.SelectedConstructor == "preferred", "ActivatorUtilities selects the preferred public constructor.");
        Require(ReferenceEquals(preferredGrain.Dependency, firstScope.ServiceProvider.GetRequiredService<GrainActivationDependency>()),
            "The preferred constructor receives the activation-scoped dependency.");

        var injected = GetActivator(nameof(GrainActivationInjectedGrain), services, map);
        var first = (GrainActivationInjectedGrain)injected.CreateInstance(firstContext);
        var second = (GrainActivationInjectedGrain)injected.CreateInstance(secondContext);
        Require(ReferenceEquals(first.Dependency, firstScope.ServiceProvider.GetRequiredService<GrainActivationDependency>()),
            "The first activation receives its scoped dependency.");
        Require(ReferenceEquals(second.Dependency, secondScope.ServiceProvider.GetRequiredService<GrainActivationDependency>()),
            "The second activation receives its scoped dependency.");
        Require(!ReferenceEquals(first.Dependency, second.Dependency), "Activation scopes have distinct dependencies.");

        await parameterless.DisposeInstance(firstContext, addition);
        await preferred.DisposeInstance(firstContext, preferredGrain);
        await injected.DisposeInstance(firstContext, first);
        await injected.DisposeInstance(secondContext, second);
        Console.WriteLine("NativeAOT grain activation passed: implicit constructor, preferred constructor, and scoped dependency identity.");
    }

    private static DefaultGrainActivator GetActivator(string name, IServiceProvider services, GrainClassMap map)
    {
        if (!map.TryGetGrainClass(GrainType.Create(name), out var grainClass))
        {
            throw new InvalidOperationException($"Generated metadata did not register grain '{name}'.");
        }

        return new DefaultGrainActivator(services, grainClass);
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "This read-only collection access consumes the generated manifest, whose AddInterfaceImplementation calls preserve the public constructors used by the production grain activator.")]
    private static ImmutableDictionary<GrainType, Type> GetRegisteredGrainClasses(TypeManifestOptions manifest)
        => manifest.InterfaceImplementations.ToImmutableDictionary(type => GrainType.Create(type.Name));

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

public interface IGrainActivationGrain : IGrainWithIntegerKey
{
    Task<int> Add(int left, int right);
}

public sealed class GrainActivationParameterlessGrain : Grain, IGrainActivationGrain
{
    public Task<int> Add(int left, int right) => Task.FromResult(left + right);
}

public sealed class GrainActivationDependency;

public sealed class GrainActivationInjectedGrain(GrainActivationDependency dependency) : Grain, IGrainActivationGrain
{
    public GrainActivationDependency Dependency { get; } = dependency;

    public Task<int> Add(int left, int right) => Task.FromResult(left + right);
}

public sealed class GrainActivationPreferredGrain : Grain, IGrainActivationGrain
{
    public GrainActivationPreferredGrain()
    {
        SelectedConstructor = "parameterless";
    }

    [ActivatorUtilitiesConstructor]
    public GrainActivationPreferredGrain(GrainActivationDependency dependency)
    {
        Dependency = dependency;
        SelectedConstructor = "preferred";
    }

    public GrainActivationDependency? Dependency { get; }

    public string SelectedConstructor { get; }

    public Task<int> Add(int left, int right) => Task.FromResult(left + right);
}

internal sealed class GrainActivationContext(IServiceProvider services) : IGrainContext
{
    public IServiceProvider ActivationServices { get; } = services;
    public GrainReference GrainReference => throw new NotSupportedException();
    public GrainId GrainId => throw new NotSupportedException();
    public object? GrainInstance => throw new NotSupportedException();
    public ActivationId ActivationId => throw new NotSupportedException();
    public GrainAddress Address => throw new NotSupportedException();
    public IGrainLifecycle ObservableLifecycle => throw new NotSupportedException();
    public IWorkItemScheduler Scheduler => throw new NotSupportedException();
    public Task Deactivated => throw new NotSupportedException();
    public object? GetTarget() => throw new NotSupportedException();
    public object? GetComponent(Type componentType) => throw new NotSupportedException();
    public void SetComponent<TComponent>(TComponent? value) where TComponent : class => throw new NotSupportedException();
    public void ReceiveMessage(object message) => throw new NotSupportedException();
    public void Activate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public void Deactivate(DeactivationReason deactivationReason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public void Rehydrate(IRehydrationContext context) => throw new NotSupportedException();
    public void Migrate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public bool Equals(IGrainContext? other) => ReferenceEquals(this, other);
}
