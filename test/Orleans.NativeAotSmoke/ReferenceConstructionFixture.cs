using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.CodeGeneration;
using Orleans.GrainReferences;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Versions;
using Orleans.Serialization;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.TypeSystem;

namespace UnitTests.GrainReferences;

internal sealed class ReferenceConstructionFixture : IDisposable
{
    public static readonly GrainType GrainType = GrainType.Create("reference-construction");
    public static readonly GrainInterfaceType InterfaceType = GrainInterfaceType.Create(nameof(IConstructionGrain));

    public ReferenceConstructionFixture(
        bool unordered = false,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.Interfaces)] Type? proxyType = null)
    {
        Runtime = new RecordingReferenceRuntime();
        Services = new ServiceCollection()
            .AddSerializer()
            // Construction uses these services for shared state; payload serialization is tested separately.
            .AddSingleton(_ => new TypeConverter([], [], [], Options.Create(new TypeManifestOptions()), new CachedTypeResolver()))
            .AddSingleton(serviceProvider => new CodecProvider(serviceProvider, Options.Create(new TypeManifestOptions())))
            .AddSingleton<IGrainReferenceRuntime>(Runtime)
            .BuildServiceProvider();
        var options = Services.GetRequiredService<IOptions<TypeManifestOptions>>();
        if (proxyType is not null)
        {
            var manifestOptions = new TypeManifestOptions();
            manifestOptions.AddInterfaceProxy(proxyType);
            options = Options.Create(manifestOptions);
        }

        var typeConverter = Services.GetRequiredService<TypeConverter>();
        var resolver = new GrainInterfaceTypeResolver([new ConstructionInterfaceTypeProvider()], typeConverter);
        var manifestProvider = new ConstructionManifestProvider(unordered);
        Provider = new GrainReferenceActivatorProvider(
            Services,
            new GrainPropertiesResolver(manifestProvider),
            new RpcProvider(options, resolver, typeConverter),
            Services.GetRequiredService<CopyContextPool>(),
            Services.GetRequiredService<CodecProvider>(),
            new GrainVersionManifest(manifestProvider));
        Activator = new GrainReferenceActivator(Services, [Provider]);
    }

    public ServiceProvider Services { get; }
    public RecordingReferenceRuntime Runtime { get; }
    public GrainReferenceActivatorProvider Provider { get; }
    public GrainReferenceActivator Activator { get; }

    public GrainReference CreateReference(string key) =>
        Activator.CreateReference(GrainId.Create(GrainType, IdSpan.Create(key)), InterfaceType);

    public void Dispose() => Services.Dispose();

    private sealed class ConstructionInterfaceTypeProvider : IGrainInterfaceTypeProvider
    {
        public bool TryGetGrainInterfaceType(Type type, out GrainInterfaceType grainInterfaceType)
        {
            grainInterfaceType = GrainInterfaceType.Create(type.Name);
            return true;
        }
    }

    private sealed class ConstructionManifestProvider : IClusterManifestProvider
    {
        public ConstructionManifestProvider(bool unordered)
        {
            var properties = ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal, StringComparer.Ordinal).Add(
                WellKnownGrainTypeProperties.Unordered, unordered ? "true" : "false");
            var interfaceProperties = ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal, StringComparer.Ordinal).Add(
                WellKnownGrainInterfaceProperties.Version, "17");
            LocalGrainManifest = new GrainManifest(
                ImmutableDictionary<GrainType, GrainProperties>.Empty.Add(GrainType, new GrainProperties(properties)),
                ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty.Add(
                    InterfaceType, new GrainInterfaceProperties(interfaceProperties)));
            Current = new ClusterManifest(
                new MajorMinorVersion(1, 0),
                ImmutableDictionary<SiloAddress, GrainManifest>.Empty,
                [LocalGrainManifest]);
        }

        public ClusterManifest Current { get; }
        public GrainManifest LocalGrainManifest { get; }
        public IAsyncEnumerable<ClusterManifest> Updates => throw new NotSupportedException();
    }
}

public interface IConstructionBaseGrain : IGrainWithStringKey;
public interface IConstructionGrain : IConstructionBaseGrain;

internal sealed class RecordingReferenceRuntime : IGrainReferenceRuntime
{
    public GrainReference? CastReference { get; private set; }
    public Type? CastInterface { get; private set; }

    public object Cast(IAddressable grain, Type interfaceType)
    {
        CastReference = (GrainReference)grain;
        CastInterface = interfaceType;
        return grain;
    }

    public ValueTask<T?> InvokeMethodAsync<T>(GrainReference reference, IInvokable request, InvokeMethodOptions options) =>
        throw new NotSupportedException();

    public ValueTask InvokeMethodAsync(GrainReference reference, IInvokable request, InvokeMethodOptions options) =>
        throw new NotSupportedException();

    public void InvokeMethod(GrainReference reference, IInvokable request, InvokeMethodOptions options) =>
        throw new NotSupportedException();
}

internal sealed class InspectableConstructionProxy : GrainReference, IConstructionGrain
{
    public InspectableConstructionProxy(GrainReferenceShared shared, IdSpan key) : base(shared, key) =>
        ConstructionShared = shared;

    public GrainReferenceShared ConstructionShared { get; }
}

internal sealed class MissingConstructorProxy : GrainReference, IConstructionGrain
{
    public MissingConstructorProxy(GrainReferenceShared shared) : base(shared, default) { }
}

internal sealed class ThrowingConstructorProxy : GrainReference, IConstructionGrain
{
    public ThrowingConstructorProxy(GrainReferenceShared shared, IdSpan key) : base(shared, key) =>
        throw new ConstructionException("proxy-constructor");
}

internal sealed class NonPublicConstructorProxy : GrainReference, IConstructionGrain
{
    private NonPublicConstructorProxy(GrainReferenceShared shared, IdSpan key) : base(shared, key) { }
}

internal sealed class ConstructionException(string message) : Exception(message);
