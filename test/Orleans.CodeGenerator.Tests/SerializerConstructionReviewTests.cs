using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Serialization;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.Serializers;

namespace Orleans.CodeGenerator.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("CodeGen")]
public sealed class SerializerConstructionReviewTests
{
    [Fact]
    public void DictionaryActivationWithoutAvailabilityPreservesCustomComparer()
    {
        var services = new ServiceCollection().AddSerializer();
        services.AddSingleton<CodecProvider>(provider => new CodecProvider(
            new ProviderWithoutAvailability(provider), provider.GetRequiredService<IOptions<TypeManifestOptions>>()));
        using var provider = services.BuildServiceProvider();
        var serializer = provider.GetRequiredService<Serializer>();
        var original = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Key"] = 42 };

        var restored = serializer.Deserialize<Dictionary<string, int>>(serializer.SerializeToArray(original));
        var copied = provider.GetRequiredService<DeepCopier>().Copy(original);

        Assert.NotNull(restored);
        Assert.NotNull(copied);
        Assert.Equal(42, restored["KEY"]);
        Assert.Equal(42, copied["KEY"]);
        Assert.Same(StringComparer.OrdinalIgnoreCase, restored.Comparer);
        Assert.Same(StringComparer.OrdinalIgnoreCase, copied.Comparer);
        Assert.NotSame(original, restored);
        Assert.NotSame(original, copied);
    }

    [Fact]
    public void CapturedKeyedFacadePreservesClosedPrecedenceAndRejectsPendingExternalLookup()
    {
        var instance = new KeyedDependency<int>();
        var services = new ServiceCollection().AddSerializer();
        services.AddKeyedSingleton<IKeyedDependency<int>>(KeyedService.AnyKey, instance);
        services.AddKeyedSingleton(typeof(IKeyedDependency<>), "requested", typeof(KeyedDependency<>));
        IServiceProvider facade = null!;
        services.Configure<TypeManifestOptions>(options =>
            options.AddSerializerService<KeyedProbe>(_ =>
                new KeyedProbe(facade.GetRequiredKeyedService<IKeyedDependency<int>>("requested"))));
        using var provider = services.BuildServiceProvider();
        var codecs = provider.GetRequiredService<CodecProvider>();
        facade = codecs.Services;

        Assert.Same(instance, provider.GetRequiredKeyedService<IKeyedDependency<int>>("requested"));
        Assert.Contains("AddSerializerService", Assert.Throws<InvalidOperationException>(
            () => OrleansGeneratedCodeHelper.GetService<KeyedProbe>(null!, codecs)).Message, StringComparison.Ordinal);
        Assert.Same(instance, facade.GetRequiredKeyedService<IKeyedDependency<int>>("requested"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void LazyProvidersKeepBuiltRegistrationsAfterSourceCollectionMutation(bool keyed, bool replaceWithFactory)
    {
        var original = new KeyedDependency<int>();
        var replacement = new KeyedDependency<int>();
        var foreign = new KeyedDependency<int>();
        var closed = new ClosedDependency();
        var services = new ServiceCollection().AddSerializer();
        var leaves = 0;
        var factoryCalls = 0;
        services.Configure<TypeManifestOptions>(options =>
        {
            options.AddSerializerService<Leaf>(_ => new Leaf(++leaves));
            options.AddSerializerService<ClosedDependency>(_ => closed);
            options.AddSerializerService<MutationProbe>(provider =>
            {
                var leaf = OrleansGeneratedCodeHelper.GetService<Leaf>(null!, provider);
                var dependency = keyed
                    ? provider.Services.GetRequiredKeyedService<IKeyedDependency<int>>("instance")
                    : provider.Services.GetRequiredService<IKeyedDependency<int>>();
                return new MutationProbe(leaf, dependency);
            });
        });
        AddInstance(original);
        using var first = services.BuildServiceProvider();
        services.Remove(services.Single(static descriptor => descriptor.ServiceType == typeof(IKeyedDependency<int>)));
        if (replaceWithFactory)
        {
            if (keyed)
                services.AddKeyedSingleton<IKeyedDependency<int>>("instance", (_, _) => CreateReplacement());
            else
                services.AddSingleton<IKeyedDependency<int>>(_ => CreateReplacement());
        }
        else
        {
            AddInstance(replacement);
        }
        services.AddSingleton<LateDependency>();
        using var second = services.BuildServiceProvider();
        services.Remove(services.Single(static descriptor => descriptor.ServiceType == typeof(IKeyedDependency<int>)));
        AddInstance(foreign);
        var firstCodecs = first.GetRequiredService<CodecProvider>();
        var secondCodecs = second.GetRequiredService<CodecProvider>();
        foreach (var codecs in new[] { firstCodecs, secondCodecs })
        {
            var failure = Assert.Throws<InvalidOperationException>(
                () => OrleansGeneratedCodeHelper.GetService<MutationProbe>(null!, codecs));
            Assert.Contains("AddSerializerService", failure.Message, StringComparison.Ordinal);
            Assert.Equal(0, factoryCalls);
            Assert.Same(closed, OrleansGeneratedCodeHelper.GetService<ClosedDependency>(null!, codecs));
            _ = OrleansGeneratedCodeHelper.GetService<Leaf>(null!, codecs);
        }
        Assert.Equal(4, leaves);
        Assert.Same(original, Resolve(firstCodecs.Services));
        Assert.Same(replacement, Resolve(secondCodecs.Services));
        Assert.Same(original, Resolve(first));
        Assert.Same(replacement, Resolve(second));
        Assert.Equal(replaceWithFactory ? 1 : 0, factoryCalls);
        Assert.False(((IServiceProviderIsService)firstCodecs.Services).IsService(typeof(LateDependency)));
        Assert.True(((IServiceProviderIsService)secondCodecs.Services).IsService(typeof(LateDependency)));

        void AddInstance(IKeyedDependency<int> value)
        {
            if (keyed) services.AddKeyedSingleton("instance", value);
            else services.AddSingleton(value);
        }
        IKeyedDependency<int> CreateReplacement()
        {
            factoryCalls++;
            return replacement;
        }
        IKeyedDependency<int> Resolve(IServiceProvider provider) => keyed
            ? provider.GetRequiredKeyedService<IKeyedDependency<int>>("instance")
            : provider.GetRequiredService<IKeyedDependency<int>>();
    }

    [Theory]
    [InlineData("activator")]
    [InlineData("value serializer")]
    [InlineData("base copier")]
    public void CaughtDirectMaterializationFailureRollsBackPendingGraph(string service)
    {
        var services = new ServiceCollection().AddSerializer();
        var leaves = 0;
        var attempts = 0;
        Leaf? failedLeaf = null;
        ArgumentException? caught = null;
        services.Configure<TypeManifestOptions>(options =>
        {
            options.AddActivator(typeof(ConstrainedActivator<>));
            options.AddSerializer(typeof(ConstrainedValueSerializer<>));
            options.AddCopier(typeof(ConstrainedBaseCopier<>));
            options.AddSerializerService<Leaf>(_ => new Leaf(++leaves));
            options.AddSerializerService<Root>(provider =>
            {
                var leaf = OrleansGeneratedCodeHelper.GetService<Leaf>(null!, provider);
                if (++attempts == 1)
                {
                    failedLeaf = leaf;
                    try
                    {
                        switch (service)
                        {
                            case "activator": _ = provider.GetActivator<ReferenceModel<int>>(); break;
                            case "value serializer": _ = provider.GetValueSerializer<ValueModel<int>>(); break;
                            case "base copier": _ = provider.GetBaseCopier<ReferenceModel<int>>(); break;
                            default: throw new InvalidOperationException(service);
                        }
                    }
                    catch (ArgumentException exception)
                    {
                        caught = exception;
                    }
                }
                return new Root(leaf);
            });
        });
        using var provider = services.BuildServiceProvider();
        var codecs = provider.GetRequiredService<CodecProvider>();
        var committed = codecs.GetCodec<int>();
        var activator = codecs.GetActivator<ReferenceModel<string>>();
        var serializer = codecs.GetValueSerializer<ValueModel<string>>();
        var copier = codecs.GetBaseCopier<ReferenceModel<string>>();

        var failure = Assert.Throws<ArgumentException>(() => OrleansGeneratedCodeHelper.GetService<Root>(null!, codecs));
        Assert.Same(caught, failure);
        Assert.Equal(1, leaves);
        var root = OrleansGeneratedCodeHelper.GetService<Root>(null!, codecs);
        Assert.Equal(2, attempts);
        Assert.Equal(2, leaves);
        Assert.Equal(2, root.Leaf.Generation);
        Assert.NotSame(failedLeaf, root.Leaf);
        Assert.Same(root.Leaf, OrleansGeneratedCodeHelper.GetService<Leaf>(null!, codecs));
        Assert.Same(root, OrleansGeneratedCodeHelper.GetService<Root>(null!, codecs));
        Assert.Same(committed, codecs.GetCodec<int>());
        Assert.Same(activator, codecs.GetActivator<ReferenceModel<string>>());
        Assert.Same(serializer, codecs.GetValueSerializer<ValueModel<string>>());
        Assert.Same(copier, codecs.GetBaseCopier<ReferenceModel<string>>());
    }

    private sealed class ProviderWithoutAvailability(IServiceProvider provider) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IServiceProviderIsService) ? null : provider.GetService(serviceType);
    }

    public interface IKeyedDependency<T>;
    public sealed class KeyedDependency<T> : IKeyedDependency<T>;
    private sealed class KeyedProbe(IKeyedDependency<int> dependency)
    {
        public IKeyedDependency<int> Dependency { get; } = dependency;
    }
    private sealed class ClosedDependency;
    private sealed class LateDependency;
    private sealed class MutationProbe(Leaf leaf, IKeyedDependency<int> dependency)
    {
        public Leaf Leaf { get; } = leaf;
        public IKeyedDependency<int> Dependency { get; } = dependency;
    }
    private sealed class Leaf(int generation)
    {
        public int Generation { get; } = generation;
    }
    private sealed class Root(Leaf leaf)
    {
        public Leaf Leaf { get; } = leaf;
    }
    public sealed class ReferenceModel<T>;
    public struct ValueModel<T>;

    public sealed class ConstrainedActivator<T> : IActivator<ReferenceModel<T>> where T : class
    {
        public ReferenceModel<T> Create() => new();
    }

    public sealed class ConstrainedValueSerializer<T> : IValueSerializer<ValueModel<T>> where T : class
    {
        public void Serialize<TBufferWriter>(ref Writer<TBufferWriter> writer, scoped ref ValueModel<T> value)
            where TBufferWriter : System.Buffers.IBufferWriter<byte> => throw new NotSupportedException();
        public void Deserialize<TInput>(ref Reader<TInput> reader, scoped ref ValueModel<T> value) => throw new NotSupportedException();
    }

    public sealed class ConstrainedBaseCopier<T> : IBaseCopier<ReferenceModel<T>> where T : class
    {
        public void DeepCopy(ReferenceModel<T> original, ReferenceModel<T> copy, CopyContext context) => throw new NotSupportedException();
    }
}
