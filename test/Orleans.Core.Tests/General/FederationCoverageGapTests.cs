using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.CodeGeneration;
using Orleans.Hosting;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
using Orleans.Serialization.Invocation;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.General;

[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestArea("Runtime")]
[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[Trait("Phase", "9")]
[Trait("FullyQualifiedName", "UnitTests.General.FederationCoverageGapTests")]
public sealed class FederationCoverageGapTests(TestEnvironmentFixture environment)
{
    private static readonly GrainId GrainId = GrainId.Create(
        GrainType.Create("phase9.test-grain"),
        GrainIdKeyExtensions.CreateIntegerKey(901));

    [Fact]
    public void IGrainFactory_DefaultUniversalOverloads_FailClosedForLegacyImplementations()
    {
        IGrainFactory factory = new LegacyGrainFactory();
        var reference = UniversalReference.CreateVirtual(
            GrainId,
            GrainInterfaceType.Create("phase9.interface"),
            "phase9-service");

        var typed = Assert.Throws<NotSupportedException>(() => factory.GetGrain<ITestGrain>(reference));
        var untyped = Assert.Throws<NotSupportedException>(() => factory.GetGrain(reference));

        Assert.Equal("This grain factory does not support universal references.", typed.Message);
        Assert.Equal(typed.Message, untyped.Message);
    }

    [Fact]
    public void UntypedUniversalReferenceOverloads_PreserveIdentityAcrossClusterClients()
    {
        var source = CreateTestGrainReference(902);
        var expected = source.GetUniversalReference();
        var internalClient = new InternalClusterClient(environment.RuntimeClient, environment.InternalGrainFactory);

        var clusterResult = ((IGrainFactory)environment.Client).GetGrain(expected);
        var internalResult = ((IGrainFactory)internalClient).GetGrain(expected);

        Assert.IsAssignableFrom<GrainReference>(clusterResult);
        Assert.IsAssignableFrom<GrainReference>(internalResult);
        Assert.Equal(expected, clusterResult.GetUniversalReference());
        Assert.Equal(expected.InterfaceType, clusterResult.GetUniversalReference().InterfaceType);
        Assert.Equal(expected, internalResult.GetUniversalReference());
        Assert.Equal(expected.InterfaceType, internalResult.GetUniversalReference().InterfaceType);
    }

    [Fact]
    public void GrainReferenceRuntime_CastActivation_CreatesReferenceFromActivationIdentity()
    {
        var context = NSubstitute.Substitute.For<IGrainContext>();
        context.GrainId.Returns(GrainId);
        var activation = new TestGrainActivation(context);

        var result = environment.InternalGrainFactory.Cast<ITestGrain>(activation);
        var actual = result.GetUniversalReference();

        Assert.Equal(GrainId, actual.GrainId);
        Assert.Equal(UniversalReferenceBinding.Virtual, actual.Binding);
        Assert.Null(actual.ClusterId);
        Assert.Equal(901, result.GetPrimaryKeyLong());
    }

    [Fact]
    public void GrainReferenceShared_LegacyConstructor_CreatesDefaultVirtualIdentity()
    {
        var template = GetTemplateShared();
        var shared = new GrainReferenceShared(
            template.GrainType,
            template.InterfaceType,
            template.InterfaceVersion,
            template.Runtime,
            template.InvokeMethodOptions,
            template.CodecProvider,
            template.CopyContextPool,
            template.ServiceProvider);

        var reference = shared.CreateUniversalReference(GrainId);

        Assert.Equal(GrainId, reference.GrainId);
        Assert.Equal(template.InterfaceType, reference.InterfaceType);
        Assert.Equal("default", reference.ServiceId);
        Assert.Equal(UniversalReferenceBinding.Virtual, reference.Binding);
        Assert.Null(reference.ClusterId);
    }

    [Fact]
    public void GrainReferenceShared_UnsupportedDefaultBinding_ThrowsExactError()
    {
        var shared = CreateShared((UniversalReferenceBinding)byte.MaxValue);

        var exception = Assert.Throws<InvalidOperationException>(() => shared.CreateUniversalReference(GrainId));

        Assert.Equal("Unsupported universal reference binding '255'.", exception.Message);
    }

    [Fact]
    public void GrainReference_UniversalConstructor_RejectsSharedTypeAndInterfaceMismatches()
    {
        var shared = CreateShared(UniversalReferenceBinding.Virtual);
        var wrongType = UniversalReference.CreateVirtual(
            GrainId.Create("phase9.other-grain", "key"),
            shared.InterfaceType,
            shared.ServiceId);
        var wrongInterface = UniversalReference.CreateVirtual(
            GrainId,
            GrainInterfaceType.Create("phase9.other-interface"),
            shared.ServiceId);

        var typeException = Assert.Throws<ArgumentException>(
            () => GrainReference.FromUniversalReference(shared, wrongType));
        var interfaceException = Assert.Throws<ArgumentException>(
            () => GrainReference.FromUniversalReference(shared, wrongInterface));

        Assert.Equal("universalReference", typeException.ParamName);
        Assert.Contains("grain type", typeException.Message, StringComparison.Ordinal);
        Assert.Equal("universalReference", interfaceException.ParamName);
        Assert.Contains("interface type", interfaceException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GrainReference_ValueSemantics_ForwardToUniversalIdentityAndFormatting()
    {
        var shared = CreateShared(UniversalReferenceBinding.Cluster);
        var identity = shared.CreateUniversalReference(GrainId);
        var reference = GrainReference.FromUniversalReference(shared, identity);
        Span<char> buffer = stackalloc char[256];
        Span<char> shortBuffer = stackalloc char[4];

        var formatted = ((ISpanFormattable)reference).TryFormat(buffer, out var written, default, null);
        var shortResult = ((ISpanFormattable)reference).TryFormat(shortBuffer, out var shortWritten, default, null);

        Assert.Equal(identity.GetHashCode(), reference.GetHashCode());
        Assert.Equal(identity.GetUniformHashCode(), reference.GetUniformHashCode());
        Assert.Equal($"GrainReference:{identity}", reference.ToString());
        Assert.Equal(reference.ToString(), ((IFormattable)reference).ToString(null, null));
        Assert.True(formatted);
        Assert.Equal(reference.ToString(), buffer[..written].ToString());
        Assert.False(shortResult);
        Assert.Equal(0, shortWritten);
    }

    [Fact]
    public void ClusterDirectoryEntry_InvalidRequiredFields_RejectEachInvalidOwnershipState()
    {
        var expiration = new DateTimeOffset(2040, 2, 3, 4, 5, 6, TimeSpan.Zero);

        var grain = Assert.Throws<ArgumentException>(
            () => new ClusterDirectoryEntry(default, "east", 1, 2, 3, expiration));
        var version = Assert.Throws<ArgumentOutOfRangeException>(
            () => new ClusterDirectoryEntry(GrainId, "east", 0, 2, 3, expiration));
        var fence = Assert.Throws<ArgumentOutOfRangeException>(
            () => new ClusterDirectoryEntry(GrainId, "east", 1, 2, 0, expiration));
        var lease = Assert.Throws<ArgumentException>(
            () => new ClusterDirectoryEntry(GrainId, "east", 1, 2, 3, default));

        Assert.Equal("grainId", grain.ParamName);
        Assert.Equal("version", version.ParamName);
        Assert.Equal("fencingToken", fence.ParamName);
        Assert.Equal("leaseExpiration", lease.ParamName);
    }

    [Fact]
    public void ClientBuilderExtensions_RegisterAndResolveEveryFederationServiceKind()
    {
        var directServices = new ServiceCollection();
        var directBuilder = new ClientBuilder(directServices, new ConfigurationBuilder().Build());
        directBuilder.AddInterClusterTransport<ApplicationTransport>();
        using var directProvider = directServices.BuildServiceProvider();

        var services = new ServiceCollection();
        var builder = new ClientBuilder(services, new ConfigurationBuilder().Build());
        builder.AddRendezvousClusterLocator("rendezvous");
        builder.AddDirectoryClusterLocator<TestDirectory>("directory");
        builder.UseClientInterClusterTransport<TestClientProvider>();
        builder.AddMetaclusterTopologyProvider<TestTopologyProvider>();
        AddDirectoryLocatorDependencies(services);
        using var provider = services.BuildServiceProvider();

        Assert.IsType<ApplicationTransport>(directProvider.GetRequiredService<IInterClusterTransport>());
        Assert.IsType<RendezvousClusterLocator>(
            provider.GetRequiredKeyedService<IClusterLocator>("rendezvous"));
        Assert.IsType<TestDirectory>(
            provider.GetRequiredKeyedService<IClusterDirectory>("directory"));
        Assert.IsType<DirectoryClusterLocator>(
            provider.GetRequiredKeyedService<IClusterLocator>("directory"));
        Assert.IsType<TestClientProvider>(provider.GetRequiredService<IInterClusterClientProvider>());
        Assert.IsType<ClientInterClusterTransport>(provider.GetRequiredService<IInterClusterTransport>());
        Assert.IsType<TestTopologyProvider>(provider.GetRequiredService<IMetaclusterTopologyProvider>());
    }

    [Fact]
    public void SiloBuilderExtensions_RegisterAndResolveEveryFederationServiceKind()
    {
        var directServices = new ServiceCollection();
        var directBuilder = new SiloBuilder(directServices, new ConfigurationBuilder().Build());
        directBuilder.AddInterClusterTransport<ApplicationTransport>();
        directBuilder.AddInterClusterRequestAuthorizer<TestAuthorizer>();
        using var directProvider = directServices.BuildServiceProvider();

        var services = new ServiceCollection();
        var builder = new SiloBuilder(services, new ConfigurationBuilder().Build());
        builder.AddRendezvousClusterLocator("rendezvous");
        builder.AddDirectoryClusterLocator<TestDirectory>("directory");
        builder.UseClientInterClusterTransport<TestClientProvider>();
        builder.AddMetaclusterTopologyProvider<TestTopologyProvider>();
        AddDirectoryLocatorDependencies(services);
        using var provider = services.BuildServiceProvider();

        Assert.IsType<ApplicationTransport>(directProvider.GetRequiredService<IInterClusterTransport>());
        Assert.IsType<TestAuthorizer>(directProvider.GetRequiredService<IInterClusterRequestAuthorizer>());
        Assert.IsType<RendezvousClusterLocator>(
            provider.GetRequiredKeyedService<IClusterLocator>("rendezvous"));
        Assert.IsType<TestDirectory>(
            provider.GetRequiredKeyedService<IClusterDirectory>("directory"));
        Assert.IsType<DirectoryClusterLocator>(
            provider.GetRequiredKeyedService<IClusterLocator>("directory"));
        Assert.IsType<TestClientProvider>(provider.GetRequiredService<IInterClusterClientProvider>());
        Assert.IsType<ClientInterClusterTransport>(provider.GetRequiredService<IInterClusterTransport>());
        Assert.IsType<TestTopologyProvider>(provider.GetRequiredService<IMetaclusterTopologyProvider>());
    }

    private ITestGrain CreateTestGrainReference(long key) =>
        environment.GrainFactory.GetGrain<ITestGrain>(
            GrainId.Create(GrainType.Create("phase9.test-grain"), GrainIdKeyExtensions.CreateIntegerKey(key)));

    private GrainReferenceShared GetTemplateShared() => ((GrainReference)CreateTestGrainReference(903)).Shared;

    private GrainReferenceShared CreateShared(UniversalReferenceBinding binding)
    {
        var template = GetTemplateShared();
        return new GrainReferenceShared(
            template.GrainType,
            template.InterfaceType,
            template.InterfaceVersion,
            template.Runtime,
            template.InvokeMethodOptions,
            template.CodecProvider,
            template.CopyContextPool,
            template.ServiceProvider,
            "phase9-service",
            "phase9-cluster",
            binding);
    }

    private static void AddDirectoryLocatorDependencies(IServiceCollection services)
    {
        var manifestProvider = Substitute.For<IClusterManifestProvider>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        services.AddSingleton(new ClusterPlacementStrategyResolver(
            new GrainPropertiesResolver(manifestProvider),
            serviceProvider));
        services.AddSingleton(new ClusterPlacementDirectorResolver(serviceProvider));
    }

    private sealed class LegacyGrainFactory : IGrainFactory
    {
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidKey => throw new NotImplementedException();

        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerKey => throw new NotImplementedException();

        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithStringKey => throw new NotImplementedException();

        public TGrainInterface GetGrain<TGrainInterface>(
            Guid primaryKey,
            string keyExtension,
            string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidCompoundKey => throw new NotImplementedException();

        public TGrainInterface GetGrain<TGrainInterface>(
            long primaryKey,
            string keyExtension,
            string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerCompoundKey => throw new NotImplementedException();

        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver => throw new NotImplementedException();

        public void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver => throw new NotImplementedException();

        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey) => throw new NotImplementedException();

        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey) => throw new NotImplementedException();

        public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey) => throw new NotImplementedException();

        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension)
            => throw new NotImplementedException();

        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension)
            => throw new NotImplementedException();

        public TGrainInterface GetGrain<TGrainInterface>(GrainId grainId)
            where TGrainInterface : IAddressable => throw new NotImplementedException();

        public IAddressable GetGrain(GrainId grainId) => throw new NotImplementedException();

        public IAddressable GetGrain(GrainId grainId, GrainInterfaceType interfaceType)
            => throw new NotImplementedException();

        public IAddressable GetGrain(Type interfaceType, IdSpan grainKey, string grainClassNamePrefix)
            => throw new NotImplementedException();

        public IAddressable GetGrain(Type interfaceType, IdSpan grainKey) => throw new NotImplementedException();
    }

    private sealed class TestGrainActivation(IGrainContext context) : IAddressable, IGrainBase
    {
        public IGrainContext GrainContext { get; } = context;
    }

    private sealed class TestDirectory : IClusterDirectory
    {
        public ValueTask<ClusterDirectoryEntry?> Lookup(
            GrainId grainId,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public ValueTask<ClusterDirectoryEntry> GetOrCreate(
            GrainId grainId,
            string proposedClusterId,
            long topologyEpoch,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public ValueTask<ClusterDirectoryEntry?> TryRenew(
            GrainId grainId,
            long expectedVersion,
            string ownerClusterId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public ValueTask<ClusterDirectoryEntry?> TryMove(
            GrainId grainId,
            long expectedVersion,
            string destinationClusterId,
            long topologyEpoch,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class TestClientProvider : IInterClusterClientProvider
    {
        public ValueTask<IClusterClient> GetClient(
            ClusterIdentity destination,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class TestTopologyProvider : IMetaclusterTopologyProvider
    {
        public ValueTask<MetaclusterTopology> GetTopology(CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public async IAsyncEnumerable<MetaclusterTopology> Watch(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield break;
        }
    }

    private sealed class ApplicationTransport : IInterClusterTransport
    {
        public ValueTask<Response> SendRequest(
            ClusterIdentity destination,
            UniversalReference target,
            IInvokable request,
            InvokeMethodOptions options,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class TestAuthorizer : IInterClusterRequestAuthorizer
    {
        public ValueTask Authorize(
            ClusterIdentity source,
            UniversalReference target,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    [Fact]
    public void GrainReferenceActivator_LegacyKeyConstructorAcceptsEquivalentLocalIdentity()
    {
        var shared = CreateShared(UniversalReferenceBinding.Virtual);
        var expected = shared.CreateUniversalReference(GrainId);
        var activator = CreateLegacyKeyActivator(shared);

        var result = activator.CreateReference(expected);

        Assert.IsType<LegacyKeyOnlyGrainReference>(result);
        Assert.Equal(expected, result.UniversalReference);
        Assert.Same(shared, result.Shared);
        Assert.Same(shared.Runtime, result.Shared.Runtime);
        Assert.Same(shared.CodecProvider, result.Shared.CodecProvider);
        Assert.Same(shared.CopyContextPool, result.Shared.CopyContextPool);
        Assert.Same(shared.ServiceProvider, result.Shared.ServiceProvider);
    }

    [Fact]
    public void GrainReferenceActivator_LegacyKeyConstructorRejectsNonEquivalentClusterIdentity()
    {
        var shared = CreateShared(UniversalReferenceBinding.Virtual);
        var remote = UniversalReference.CreateCluster(
            GrainId,
            shared.InterfaceType,
            shared.ServiceId,
            "remote-cluster");
        var activator = CreateLegacyKeyActivator(shared);

        var exception = Assert.Throws<NotSupportedException>(() => activator.CreateReference(remote));

        Assert.Contains("does not support cluster-bound references", exception.Message, StringComparison.Ordinal);
        Assert.Equal(UniversalReferenceBinding.Cluster, remote.Binding);
        Assert.NotEqual(shared.CreateUniversalReference(GrainId), remote);
    }

    private static Orleans.GrainReferences.IGrainReferenceActivator CreateLegacyKeyActivator(GrainReferenceShared shared)
    {
        var activatorType = typeof(Orleans.GrainReferences.GrainReferenceActivatorProvider).GetNestedType(
            "GrainReferenceActivator",
            System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(activatorType);

        return Assert.IsAssignableFrom<Orleans.GrainReferences.IGrainReferenceActivator>(
            Activator.CreateInstance(
                activatorType!,
                System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic,
                binder: null,
                [typeof(LegacyKeyOnlyGrainReference), shared],
                culture: null));
    }

    private sealed class LegacyKeyOnlyGrainReference(GrainReferenceShared shared, IdSpan key)
        : GrainReference(shared, key);
}
