using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.CodeGeneration;
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
        Assert.Equal("ProxyActivator", activator.GetType().Name);
        Assert.Same(first.GetType(), activator.GetType().DeclaringType);
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
