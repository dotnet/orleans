using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.CodeGeneration;
using Orleans.GrainReferences;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.Serializers;
using Xunit;

namespace UnitTests.GrainReferences;

[TestCategory("BVT"), TestCategory("GrainReference")]
public class GeneratedProxySharedCopierTests
{
    private static readonly GrainInterfaceType InterfaceType = GrainInterfaceType.Create(nameof(ICopyingConstructionGrain));

    [Fact]
    public void ReferencesShareOneResolutionOfProxyCopiers()
    {
        var constructions = 0;
        using var fixture = new ReferenceConstructionFixture(configureServices: services =>
            services.AddTransient(serviceProvider =>
            {
                constructions++;
                return ActivatorUtilities.CreateInstance<OrleansCodeGen.UnitTests.GrainReferences.Copier_ConstructionPayload>(serviceProvider);
            }));
        var first = CreateReference(fixture, "first");
        var second = CreateReference(fixture, "second");

        Assert.Same(first.Shared, second.Shared);
        Assert.NotEqual(typeof(GrainReferenceShared), first.Shared.GetType());
        Assert.Equal(1, constructions);
        Assert.True(fixture.Provider.TryGet(ReferenceConstructionFixture.GrainType, InterfaceType, out var activator));
        Assert.Equal("ProxyActivator_ICopyingConstructionGrain", activator.GetType().Name);
        Assert.Equal(ReferenceConstructionFixture.GrainType, first.Shared.GrainType);
        Assert.Equal(InterfaceType, first.Shared.InterfaceType);
        Assert.Same(fixture.Runtime, first.Shared.Runtime);
        Assert.Same(fixture.Services, first.Shared.ServiceProvider);
        Assert.Same(fixture.Services.GetRequiredService<CodecProvider>(), first.Shared.CodecProvider);
        Assert.Same(fixture.Services.GetRequiredService<CopyContextPool>(), first.Shared.CopyContextPool);
        Assert.Equal(InvokeMethodOptions.None, first.Shared.InvokeMethodOptions);
        Assert.Equal(GrainId.Create(ReferenceConstructionFixture.GrainType, IdSpan.Create("second")), second.GrainId);

        var input = new ConstructionPayload { Value = 42 };
        input.Next = input;
        _ = ((ICopyingConstructionGrain)second).Send(input);
        var copy = Assert.IsType<ConstructionPayload>(fixture.Runtime.LastRequest!.GetArgument(0));
        Assert.NotSame(input, copy);
        Assert.Equal(42, copy.Value);
        Assert.Same(copy, copy.Next);
    }

    [Fact]
    public void FailedCopierResolutionSurfacesAndRetries()
    {
        var calls = 0;
        var failure = new InvalidOperationException("Injected copier construction failure");
        using var fixture = new ReferenceConstructionFixture(configureServices: services =>
            services.AddTransient(serviceProvider =>
            {
                if (++calls == 1)
                {
                    throw failure;
                }

                return ActivatorUtilities.CreateInstance<OrleansCodeGen.UnitTests.GrainReferences.Copier_ConstructionPayload>(serviceProvider);
            }));

        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => CreateReference(fixture, "first")));
        var reference = CreateReference(fixture, "first");
        Assert.Same(reference.Shared, CreateReference(fixture, "second").Shared);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(InvokeMethodOptions.None)]
    [InlineData(InvokeMethodOptions.Unordered)]
    public void DerivedSharedStateIsInitializedFromActivatorArguments(InvokeMethodOptions invokeMethodOptions)
    {
        using var fixture = new ReferenceConstructionFixture();
        var options = fixture.Services.GetRequiredService<IOptions<TypeManifestOptions>>().Value;
        var proxyType = Assert.Single(options.InterfaceProxyTypes, typeof(ICopyingConstructionGrain).IsAssignableFrom);
        var codecProvider = fixture.Services.GetRequiredService<CodecProvider>();
        var copyContextPool = fixture.Services.GetRequiredService<CopyContextPool>();
        var grainType = GrainType.Create("direct-shared-construction");
        var activator = GrainReferenceActivatorProvider.CreateActivator(
            proxyType, options.InterfaceProxyActivatorTypes[proxyType],
            grainType, InterfaceType, interfaceVersion: 29, fixture.Runtime,
            invokeMethodOptions, codecProvider, copyContextPool, fixture.Services);
        var first = activator.CreateReference(GrainId.Create(grainType, IdSpan.Create("first")));
        var second = activator.CreateReference(GrainId.Create(grainType, IdSpan.Create("second")));

        Assert.NotEqual(typeof(GrainReferenceShared), first.Shared.GetType());
        Assert.Same(first.Shared, second.Shared);
        Assert.Equal(grainType, first.Shared.GrainType);
        Assert.Equal(InterfaceType, first.Shared.InterfaceType);
        Assert.Equal(29, first.Shared.InterfaceVersion);
        Assert.Equal(invokeMethodOptions, first.Shared.InvokeMethodOptions);
        Assert.Same(fixture.Runtime, first.Shared.Runtime);
        Assert.Same(codecProvider, first.Shared.CodecProvider);
        Assert.Same(copyContextPool, first.Shared.CopyContextPool);
        Assert.Same(fixture.Services, first.Shared.ServiceProvider);
    }

    [Fact]
    public void ReferencesDoNotRetainCopiersFromAnUnpublishedConstructionGraph()
    {
        ReferenceConstructionFixture? fixture = null;
        GrainReference? pending = null;
        var calls = 0;
        fixture = new ReferenceConstructionFixture(configureServices: services =>
        {
            services.Configure<TypeManifestOptions>(options => options.AddSerializer<UnrelatedPayload>(
                _ => throw new NotSupportedException(), _ =>
                {
                    if (++calls == 1)
                    {
                        pending = CreateReference(fixture!, "pending");
                        throw new InvalidOperationException("Injected copier construction failure");
                    }

                    return new ShallowCopier<UnrelatedPayload>();
                }));
            services.AddSingleton(serviceProvider => new CodecProvider(serviceProvider, serviceProvider.GetRequiredService<IOptions<TypeManifestOptions>>()));
        });
        using (fixture)
        {
            var provider = fixture.Services.GetRequiredService<CodecProvider>();
            Assert.Throws<InvalidOperationException>(() => provider.GetDeepCopier<UnrelatedPayload>());
            Assert.NotNull(pending);

            var reference = CreateReference(fixture, "first");
            Assert.NotSame(pending.Shared, reference.Shared);
            Assert.Same(reference.Shared, CreateReference(fixture, "second").Shared);
        }
    }

    [Fact]
    public async Task SerializerFactoryCanCreateReferenceWhileAnotherActivatorResolvesCopiers()
    {
        using var activatorEntered = new ManualResetEventSlim();
        using var graphEntered = new ManualResetEventSlim();
        using var resolvingCopier = new ManualResetEventSlim();
        GrainReferenceActivator? activator = null;
        var nestedType = GrainType.Create("nested-reference-construction");
        using var fixture = new ReferenceConstructionFixture(configureServices: services =>
        {
            services.Configure<TypeManifestOptions>(options => options.AddSerializer<UnrelatedPayload>(
                _ => throw new NotSupportedException(), _ =>
                {
                    graphEntered.Set();
                    Assert.True(resolvingCopier.Wait(TimeSpan.FromSeconds(10)));
                    activator!.CreateReference(GrainId.Create(nestedType, IdSpan.Create("nested")), ReferenceConstructionFixture.InterfaceType);
                    return new ShallowCopier<UnrelatedPayload>();
                }));
            services.AddSingleton(serviceProvider => new CodecProvider(serviceProvider, serviceProvider.GetRequiredService<IOptions<TypeManifestOptions>>()));
        });
        var codecProvider = fixture.Services.GetRequiredService<CodecProvider>();
        Assert.True(fixture.Provider.TryGet(ReferenceConstructionFixture.GrainType, ReferenceConstructionFixture.InterfaceType, out var referenceActivator));
        activator = new GrainReferenceActivator(fixture.Services, [new InterleavingActivatorProvider(
            grainType =>
            {
                if (grainType == ReferenceConstructionFixture.GrainType)
                {
                    activatorEntered.Set();
                    Assert.True(graphEntered.Wait(TimeSpan.FromSeconds(10)));
                    resolvingCopier.Set();
                    codecProvider.GetDeepCopier<UnrelatedPayload>();
                }

                return referenceActivator;
            })]);

        var first = Task.Run(() => activator.CreateReference(
            GrainId.Create(ReferenceConstructionFixture.GrainType, IdSpan.Create("first")), ReferenceConstructionFixture.InterfaceType));
        Assert.True(activatorEntered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        var second = Task.Run(() => codecProvider.GetDeepCopier<UnrelatedPayload>(), TestContext.Current.CancellationToken);

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(ReferenceConstructionFixture.GrainType, (await first).GrainId.Type);
    }

    private sealed class InterleavingActivatorProvider(Func<GrainType, IGrainReferenceActivator> create) : IGrainReferenceActivatorProvider
    {
        public bool TryGet(GrainType grainType, GrainInterfaceType interfaceType, out IGrainReferenceActivator activator)
        {
            activator = create(grainType);
            return true;
        }
    }

    private static GrainReference CreateReference(ReferenceConstructionFixture fixture, string key) =>
        fixture.CreateReference(key, InterfaceType);

    private sealed class UnrelatedPayload;
}

public interface ICopyingConstructionGrain : IGrainWithStringKey
{
    ValueTask Send(ConstructionPayload value);
}

[GenerateSerializer]
public sealed class ConstructionPayload
{
    [Id(0)] public int Value { get; set; }
    [Id(1)] public ConstructionPayload? Next { get; set; }
}
