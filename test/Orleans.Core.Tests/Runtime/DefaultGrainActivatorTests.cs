using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans;
using Orleans.Configuration;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.TypeSystem;
using TestExtensions;
using Xunit;

namespace UnitTests.Runtime;

public class DefaultGrainActivatorTests
{
    [Fact, TestCategory("BVT")]
    public void CreatesGrainWithImplicitPublicParameterlessConstructor()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var context = CreateContext(services);
        var activator = CreateActivator<ParameterlessGrain>(services);

        var grain = Assert.IsType<ParameterlessGrain>(activator.CreateInstance(context));

        Assert.Equal(42, grain.Add(17, 25));
    }

    [Fact, TestCategory("BVT")]
    public void UsesPreferredPublicConstructor()
    {
        using var services = new ServiceCollection().AddScoped<Dependency>().BuildServiceProvider();
        using var scope = services.CreateScope();
        var context = CreateContext(scope.ServiceProvider);
        var activator = CreateActivator<PreferredConstructorGrain>(services);

        var grain = Assert.IsType<PreferredConstructorGrain>(activator.CreateInstance(context));

        Assert.Same(scope.ServiceProvider.GetRequiredService<Dependency>(), grain.Dependency);
        Assert.Equal("preferred", grain.SelectedConstructor);
    }

    [Fact, TestCategory("BVT")]
    public void ResolvesDependenciesFromEachActivationScope()
    {
        using var services = new ServiceCollection().AddScoped<Dependency>().BuildServiceProvider();
        using var firstScope = services.CreateScope();
        using var secondScope = services.CreateScope();
        var activator = CreateActivator<InjectedGrain>(services);

        var first = Assert.IsType<InjectedGrain>(activator.CreateInstance(CreateContext(firstScope.ServiceProvider)));
        var second = Assert.IsType<InjectedGrain>(activator.CreateInstance(CreateContext(secondScope.ServiceProvider)));

        Assert.Same(firstScope.ServiceProvider.GetRequiredService<Dependency>(), first.Dependency);
        Assert.Same(secondScope.ServiceProvider.GetRequiredService<Dependency>(), second.Dependency);
        Assert.NotSame(first.Dependency, second.Dependency);
        Assert.NotSame(first, second);
    }

    [Fact, TestCategory("BVT")]
    public void ManualRegistrationCreatesGrainThroughSiloManifest()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var options = new GrainTypeOptions();
        options.AddClass(typeof(ParameterlessGrain));
        var converter = new TypeConverter([], [], [], Options.Create(new TypeManifestOptions()), new CachedTypeResolver());
        var resolver = new GrainTypeResolver([], converter);
        var manifest = new SiloManifestProvider(
            [], [], Options.Create(options), resolver, new GrainInterfaceTypeResolver([], converter), converter);

        Assert.True(manifest.GrainTypeMap.TryGetGrainClass(resolver.GetGrainType(typeof(ParameterlessGrain)), out var grainClass));
        var activator = new DefaultGrainActivator(services, grainClass);
        var grain = Assert.IsType<ParameterlessGrain>(activator.CreateInstance(CreateContext(services)));

        Assert.Equal(42, grain.Add(17, 25));
        Assert.Single(manifest.SiloManifest.Grains);
    }

    [Fact, TestCategory("BVT")]
    public void ManualRegistrationExposesConstructorPreservationContract()
    {
        var registration = typeof(GrainTypeOptions).GetMethod(nameof(GrainTypeOptions.AddClass))!;
        var preservation = registration.GetParameters()[0].GetCustomAttribute<DynamicallyAccessedMembersAttribute>();
        Assert.Equal(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.Interfaces, preservation?.MemberTypes);
        var collectionWarning = typeof(GrainTypeOptions).GetProperty(nameof(GrainTypeOptions.Classes))!
            .GetMethod!.GetCustomAttribute<RequiresUnreferencedCodeAttribute>();
        Assert.NotNull(collectionWarning);
        Assert.Contains(nameof(GrainTypeOptions.AddClass), collectionWarning.Message);
        var dictionaryWarning = typeof(GrainClassMap).GetConstructors().Single()
            .GetCustomAttribute<RequiresUnreferencedCodeAttribute>();
        Assert.NotNull(dictionaryWarning);
        Assert.Contains(nameof(GrainTypeOptions.AddClass), dictionaryWarning.Message);
    }

    [Fact, TestCategory("BVT")]
    public void ManualRegistrationRejectsNullClass()
    {
        var options = new GrainTypeOptions();

        var exception = Assert.Throws<ArgumentNullException>(() => options.AddClass(null!));

        Assert.Equal("grainClass", exception.ParamName);
        Assert.Empty(options.GrainClasses);
    }

    private static IGrainContext CreateContext(IServiceProvider services)
    {
        var context = Substitute.For<IGrainContext>();
        context.ActivationServices.Returns(services);
        return context;
    }

    private static DefaultGrainActivator CreateActivator<TGrain>(IServiceProvider services)
    {
        var manifest = new TypeManifestOptions();
        manifest.AddInterfaceImplementation(typeof(TGrain));
        var grainType = GrainType.Create(typeof(TGrain).Name);
        var converter = new TypeConverter([], [], [], Options.Create(manifest), new CachedTypeResolver());
        var map = GrainClassMap.CreateRegistered(converter, ImmutableDictionary<GrainType, Type>.Empty.Add(grainType, typeof(TGrain)));
        Assert.True(map.TryGetGrainClass(grainType, out var grainClass));
        Assert.Equal(typeof(TGrain), grainClass);
        return new DefaultGrainActivator(services, grainClass);
    }

    public sealed class ParameterlessGrain : Grain
    {
        public int Add(int left, int right) => left + right;
    }

    public sealed class Dependency;

    public sealed class InjectedGrain(Dependency dependency) : Grain
    {
        public Dependency Dependency { get; } = dependency;
    }

    public sealed class PreferredConstructorGrain : Grain
    {
        public PreferredConstructorGrain()
        {
            SelectedConstructor = "parameterless";
        }

        [ActivatorUtilitiesConstructor]
        public PreferredConstructorGrain(Dependency dependency)
        {
            Dependency = dependency;
            SelectedConstructor = "preferred";
        }

        public Dependency? Dependency { get; }

        public string SelectedConstructor { get; }
    }
}
