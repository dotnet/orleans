using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Serialization;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.Serializers;

namespace Orleans.CodeGenerator.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("CodeGen")]
public sealed class SerializerConstructionReviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DiFirstRecursiveListServicesReuseCanonicalSingletons(bool copierFirst)
    {
        var node = new DiFirstNode { Value = 42 };
        node.Children = [node, node];
        node.Alias = node.Children;
        var (restored, copied) = ResolveDiFirstListServices(node.Children, copierFirst);
        foreach (var result in new[] { restored, copied })
        {
            Assert.Equal(42, result[0].Value);
            Assert.Same(result, result[0].Children);
            Assert.Same(result, result[0].Alias);
            Assert.Same(result[0], result[1]);
        }
        copied[0].Value = 23;
        Assert.Equal(42, node.Value);
        Assert.Equal(23, copied[1].Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DiFirstGenericRecursiveListServicesReuseCanonicalSingletons(bool copierFirst)
    {
        var node = new DiFirstGenericNode<int> { Value = 42 };
        node.Children = [node, node];
        node.Alias = node.Children;
        var (restored, copied) = ResolveDiFirstListServices(node.Children, copierFirst);
        foreach (var result in new[] { restored, copied })
        {
            Assert.Equal(42, result[0].Value);
            Assert.Same(result, result[0].Children);
            Assert.Same(result, result[0].Alias);
            Assert.Same(result[0], result[1]);
        }
        copied[0].Value = 23;
        Assert.Equal(42, node.Value);
        Assert.Equal(23, copied[1].Value);
    }

    private static (List<TNode> Restored, List<TNode> Copied) ResolveDiFirstListServices<TNode>(List<TNode> input, bool copierFirst) where TNode : class
    {
        var codecCalls = 0;
        var copierCalls = 0;
        var registrations = new ServiceCollection().AddSerializer();
        registrations.AddSingleton<ListCodec<TNode>>(services =>
        {
            Assert.Equal(1, ++codecCalls);
            return new ListCodec<TNode>(services.GetRequiredService<IFieldCodec<TNode>>());
        });
        registrations.AddSingleton<ListCopier<TNode>>(services =>
        {
            Assert.Equal(1, ++copierCalls);
            return new ListCopier<TNode>(services.GetRequiredService<IDeepCopier<TNode>>());
        });
        using var services = registrations.BuildServiceProvider();
        if (copierFirst) _ = services.GetRequiredService<ListCopier<TNode>>();
        else _ = services.GetRequiredService<ListCodec<TNode>>();
        var provider = services.GetRequiredService<CodecProvider>();
        var codec = services.GetRequiredService<ListCodec<TNode>>();
        var copier = services.GetRequiredService<ListCopier<TNode>>();
        var nodeCodec = provider.GetCodec<TNode>();
        var nodeCopier = provider.GetDeepCopier<TNode>();
        Assert.Same(codec, provider.GetCodec<List<TNode>>());
        Assert.Same(copier, provider.GetDeepCopier<List<TNode>>());
        Assert.Same(codec, Assert.Single(nodeCodec.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(ListCodec<TNode>)).GetValue(nodeCodec));
        Assert.Same(copier, Assert.Single(nodeCopier.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(ListCopier<TNode>)).GetValue(nodeCopier));
        Assert.Equal(1, codecCalls);
        Assert.Equal(1, copierCalls);
        var serializer = services.GetRequiredService<Serializer>();
        var restored = serializer.Deserialize<List<TNode>>(serializer.SerializeToArray(input))!;
        var copied = services.GetRequiredService<DeepCopier>().Copy(input)!;
        Assert.NotSame(input, restored);
        Assert.NotSame(input, copied);
        Assert.NotSame(input[0], restored[0]);
        Assert.NotSame(input[0], copied[0]);
        Assert.Equal(1, codecCalls);
        Assert.Equal(1, copierCalls);
        return (restored, copied);
    }

    [GenerateSerializer]
    public sealed class DiFirstNode
    {
        [Id(0)] public int Value { get; set; }
        [Id(1)] public List<DiFirstNode> Children { get; set; } = new();
        [Id(2)] public List<DiFirstNode> Alias { get; set; } = new();
    }

    [GenerateSerializer]
    public sealed class DiFirstGenericNode<T>
    {
        [Id(0)] public T Value { get; set; } = default!;
        [Id(1)] public List<DiFirstGenericNode<T>> Children { get; set; } = new();
        [Id(2)] public List<DiFirstGenericNode<T>> Alias { get; set; } = new();
    }

    [Fact]
    public void ForeignHoldersKeepCallerOwnershipInItsOriginalProvider()
    {
        var leaf = new ForeignHolderLeaf();
        var secondRegistrations = new ServiceCollection().AddSerializer();
        secondRegistrations.AddSingleton(leaf);
        secondRegistrations.Configure<TypeManifestOptions>(options =>
        {
            options.AddFieldCodec(typeof(ForeignHolderLeaf));
            options.AddCopier(typeof(ForeignHolderLeaf));
        });
        using var second = secondRegistrations.BuildServiceProvider();
        var codecHolder = second.GetRequiredService<IFieldCodec<ForeignHolderValue>>();
        var copierHolder = second.GetRequiredService<IDeepCopier<ForeignHolderValue>>();
        var firstRegistrations = new ServiceCollection().AddSerializer();
        firstRegistrations.Configure<TypeManifestOptions>(options =>
            options.AddSerializerService<ForeignHolderRoot>(_ => new ForeignHolderRoot(codecHolder, copierHolder)));
        using var first = firstRegistrations.BuildServiceProvider();
        var firstProvider = first.GetRequiredService<CodecProvider>();
        var secondProvider = second.GetRequiredService<CodecProvider>();
        var root = OrleansGeneratedCodeHelper.GetService<ForeignHolderRoot>(null!, firstProvider);
        Assert.Same(leaf, root.Codec);
        Assert.Same(leaf, root.Copier);
        Assert.NotSame(root, root.Codec);
        Assert.NotSame(root, root.Copier);
        Assert.Same(leaf, secondProvider.GetCodec<ForeignHolderValue>());
        Assert.Same(leaf, secondProvider.GetDeepCopier<ForeignHolderValue>());
        Assert.Same(root, OrleansGeneratedCodeHelper.GetService<ForeignHolderRoot>(null!, firstProvider));
    }

    [Fact]
    public void CaughtForeignHolderFailureFaultsTheCallerGraphAndRetriesFreshDependencies()
    {
        var error = new InvalidOperationException("Foreign holder construction failed.");
        var secondRegistrations = new ServiceCollection().AddSerializer();
        secondRegistrations.AddSingleton<ForeignHolderLeaf>(_ => throw error);
        secondRegistrations.Configure<TypeManifestOptions>(options => options.AddFieldCodec(typeof(ForeignHolderLeaf)));
        using var second = secondRegistrations.BuildServiceProvider();
        var holder = second.GetRequiredService<IFieldCodec<ForeignHolderValue>>();
        var attempts = 0;
        var leaves = 0;
        Leaf? failedLeaf = null;
        InvalidOperationException? caught = null;
        var firstRegistrations = new ServiceCollection().AddSerializer();
        firstRegistrations.Configure<TypeManifestOptions>(options =>
        {
            options.AddSerializerService<Leaf>(_ => new Leaf(++leaves));
            options.AddSerializerService<Root>(provider =>
            {
                var leaf = OrleansGeneratedCodeHelper.GetService<Leaf>(null!, provider);
                if (++attempts == 1)
                {
                    failedLeaf = leaf;
                    try
                    {
                        _ = OrleansGeneratedCodeHelper.UnwrapService(new ForeignHolderLeaf(), holder);
                    }
                    catch (InvalidOperationException exception)
                    {
                        caught = exception;
                    }
                }
                return new Root(leaf);
            });
        });
        using var first = firstRegistrations.BuildServiceProvider();
        var provider = first.GetRequiredService<CodecProvider>();
        var failure = Assert.Throws<InvalidOperationException>(() => OrleansGeneratedCodeHelper.GetService<Root>(null!, provider));
        Assert.Same(error, failure);
        Assert.Same(caught, failure);
        var root = OrleansGeneratedCodeHelper.GetService<Root>(null!, provider);
        Assert.Equal(2, attempts);
        Assert.Equal(2, leaves);
        Assert.NotSame(failedLeaf, root.Leaf);
        Assert.Same(root.Leaf, OrleansGeneratedCodeHelper.GetService<Leaf>(null!, provider));
        Assert.Same(root, OrleansGeneratedCodeHelper.GetService<Root>(null!, provider));
    }

    public sealed class ForeignHolderValue;

    public class ForeignHolderLeaf : IFieldCodec<ForeignHolderValue>, IDeepCopier<ForeignHolderValue>
    {
        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta,
            [System.Diagnostics.CodeAnalysis.AllowNull] Type expectedType,
            [System.Diagnostics.CodeAnalysis.AllowNull] ForeignHolderValue value)
            where TBufferWriter : System.Buffers.IBufferWriter<byte> => throw new NotSupportedException();
        public ForeignHolderValue ReadValue<TInput>(ref Reader<TInput> reader, Orleans.Serialization.WireProtocol.Field field)
            => throw new NotSupportedException();
        public ForeignHolderValue? DeepCopy(ForeignHolderValue? input, CopyContext context) => throw new NotSupportedException();
    }

    private sealed class ForeignHolderRoot : ForeignHolderLeaf
    {
        public IFieldCodec<ForeignHolderValue> Codec { get; }
        public IDeepCopier<ForeignHolderValue> Copier { get; }
        public ForeignHolderRoot(IFieldCodec<ForeignHolderValue> codec, IDeepCopier<ForeignHolderValue> copier)
        {
            Codec = OrleansGeneratedCodeHelper.UnwrapService(this, codec);
            Copier = OrleansGeneratedCodeHelper.UnwrapService(this, copier);
        }
    }

    [Fact]
    public void InitializationCallbacksResolveClosedServicesUnderTheExistingInitializationLock()
    {
        var constructions = 0;
        var callbacks = 0;
        Leaf? initializedLeaf = null;
        var services = new ServiceCollection().AddSerializer();
        services.Configure<TypeManifestOptions>(options =>
            options.AddSerializerService<Leaf>(_ => new Leaf(++constructions)));
        services.AddSingleton<IGeneralizedCodec>(serviceProvider =>
        {
            callbacks++;
            initializedLeaf = OrleansGeneratedCodeHelper.GetService<Leaf>(
                null!, serviceProvider.GetRequiredService<CodecProvider>());
            return new InitializationCodec();
        });
        using var serviceProvider = services.BuildServiceProvider();
        var codecs = serviceProvider.GetRequiredService<CodecProvider>();
        var leaf = OrleansGeneratedCodeHelper.GetService<Leaf>(null!, codecs);
        Assert.Same(initializedLeaf, leaf);
        Assert.Equal(1, constructions);
        Assert.Equal(1, callbacks);
        Assert.Same(leaf, OrleansGeneratedCodeHelper.GetService<Leaf>(null!, codecs));
    }

    [Fact]
    public void PendingImplementationCachesRollBackTogetherAndPublishCanonicalServicesOnRetry()
    {
        var attempts = 0;
        IActivator<ReferenceModel<string>>? failedActivator = null;
        IValueSerializer<ValueModel<string>>? failedSerializer = null;
        IBaseCopier<ReferenceModel<string>>? failedCopier = null;
        InvalidOperationException? caught = null;
        var services = new ServiceCollection().AddSerializer();
        services.AddSingleton<ExternalDependency>();
        services.Configure<TypeManifestOptions>(options =>
        {
            options.AddActivator(typeof(ConstrainedActivator<>));
            options.AddSerializer(typeof(ConstrainedValueSerializer<>));
            options.AddCopier(typeof(ConstrainedBaseCopier<>));
            options.AddSerializerService<ConstrainedActivator<string>>(_ => new ConstrainedActivator<string>());
            options.AddSerializerService<ConstrainedValueSerializer<string>>(_ => new ConstrainedValueSerializer<string>());
            options.AddSerializerService<ConstrainedBaseCopier<string>>(_ => new ConstrainedBaseCopier<string>());
            options.AddSerializerService<ImplementationServices>(provider =>
            {
                var result = new ImplementationServices(provider.GetActivator<ReferenceModel<string>>(),
                    provider.GetValueSerializer<ValueModel<string>>(), provider.GetBaseCopier<ReferenceModel<string>>());
                if (++attempts == 1)
                {
                    failedActivator = result.Activator;
                    failedSerializer = result.Serializer;
                    failedCopier = result.Copier;
                    try
                    {
                        _ = provider.Services.GetRequiredService<ExternalDependency>();
                    }
                    catch (InvalidOperationException exception)
                    {
                        caught = exception;
                    }
                }
                return result;
            });
        });
        using var serviceProvider = services.BuildServiceProvider();
        var codecs = serviceProvider.GetRequiredService<CodecProvider>();
        var failure = Assert.Throws<InvalidOperationException>(
            () => OrleansGeneratedCodeHelper.GetService<ImplementationServices>(null!, codecs));
        Assert.Same(caught, failure);
        var result = OrleansGeneratedCodeHelper.GetService<ImplementationServices>(null!, codecs);
        Assert.Equal(2, attempts);
        Assert.NotSame(failedActivator, result.Activator);
        Assert.NotSame(failedSerializer, result.Serializer);
        Assert.NotSame(failedCopier, result.Copier);
        Assert.Same(result.Activator, codecs.GetActivator<ReferenceModel<string>>());
        Assert.Same(result.Serializer, codecs.GetValueSerializer<ValueModel<string>>());
        Assert.Same(result.Copier, codecs.GetBaseCopier<ReferenceModel<string>>());
        Assert.Same(result.Activator, OrleansGeneratedCodeHelper.GetService<ConstrainedActivator<string>>(null!, codecs));
        Assert.Same(result.Serializer, OrleansGeneratedCodeHelper.GetService<ConstrainedValueSerializer<string>>(null!, codecs));
        Assert.Same(result.Copier, OrleansGeneratedCodeHelper.GetService<ConstrainedBaseCopier<string>>(null!, codecs));
        Assert.Same(result, OrleansGeneratedCodeHelper.GetService<ImplementationServices>(null!, codecs));
        Assert.NotNull(codecs.Services.GetRequiredService<ExternalDependency>());
    }

    private sealed class ImplementationServices(IActivator<ReferenceModel<string>> activator,
        IValueSerializer<ValueModel<string>> serializer, IBaseCopier<ReferenceModel<string>> copier)
    {
        public IActivator<ReferenceModel<string>> Activator { get; } = activator;
        public IValueSerializer<ValueModel<string>> Serializer { get; } = serializer;
        public IBaseCopier<ReferenceModel<string>> Copier { get; } = copier;
    }

    private sealed class InitializationCodec : IGeneralizedCodec
    {
        public bool IsSupportedType(Type type) => false;
        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta,
            [System.Diagnostics.CodeAnalysis.AllowNull] Type expectedType,
            [System.Diagnostics.CodeAnalysis.AllowNull] object value)
            where TBufferWriter : System.Buffers.IBufferWriter<byte> => throw new NotSupportedException();
        public object ReadValue<TInput>(ref Reader<TInput> reader, Orleans.Serialization.WireProtocol.Field field)
            => throw new NotSupportedException();
    }

    [Fact]
    public void OrdinaryMetadataRootRetainsExternalConstructorDependencies()
    {
        var calls = 0;
        var dependency = new ExternalDependency();
        var services = new ServiceCollection().AddSerializer();
        services.AddSingleton<ExternalDependency>(_ =>
        {
            calls++;
            return dependency;
        });
        services.Configure<TypeManifestOptions>(options =>
        {
            options.AddFieldCodec(typeof(ExternalConstructorCodec));
            options.AddSerializerService<Leaf>(_ => new Leaf(1));
        });
        using var scope = services.BuildServiceProvider();
        var provider = scope.GetRequiredService<CodecProvider>();
        var codec = Assert.IsType<ExternalConstructorCodec>(provider.GetCodec<ExternalConstructorValue>());
        Assert.Same(dependency, codec.Dependency);
        Assert.Equal(1, calls);
    }

    public sealed class ExternalConstructorValue;
    public sealed class ExternalConstructorCodec : IFieldCodec<ExternalConstructorValue>
    {
        public ExternalDependency Dependency { get; }
        public ExternalConstructorCodec(ExternalDependency dependency) => Dependency = dependency;
        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta,
            [System.Diagnostics.CodeAnalysis.AllowNull] Type expectedType,
            [System.Diagnostics.CodeAnalysis.AllowNull] ExternalConstructorValue value)
            where TBufferWriter : System.Buffers.IBufferWriter<byte> => throw new NotSupportedException();
        public ExternalConstructorValue ReadValue<TInput>(ref Reader<TInput> reader, Orleans.Serialization.WireProtocol.Field field)
            => throw new NotSupportedException();
    }

    [Fact]
    public void NestedProvidersKeepPartiallyConstructedCallerOwnership()
    {
        var secondServices = new ServiceCollection().AddSerializer();
        secondServices.Configure<TypeManifestOptions>(options =>
            options.AddSerializerService<ProviderOwnedNode>(_ => new ProviderOwnedNode(2)));
        using var second = secondServices.BuildServiceProvider();
        var secondCodecs = second.GetRequiredService<CodecProvider>();
        var firstServices = new ServiceCollection().AddSerializer();
        firstServices.Configure<TypeManifestOptions>(options =>
            options.AddSerializerService<ProviderOwnedNode>(provider => new ProviderOwnedNode(provider, secondCodecs)));
        using var first = firstServices.BuildServiceProvider();
        var firstCodecs = first.GetRequiredService<CodecProvider>();
        var node = OrleansGeneratedCodeHelper.GetService<ProviderOwnedNode>(null!, firstCodecs);
        Assert.Equal(1, node.Owner);
        Assert.Equal(2, node.Other!.Owner);
        Assert.NotSame(node, node.Other);
        Assert.Same(node.Other, OrleansGeneratedCodeHelper.GetService<ProviderOwnedNode>(null!, secondCodecs));
        Assert.Same(node, OrleansGeneratedCodeHelper.GetService<ProviderOwnedNode>(null!, firstCodecs));
    }

    private sealed class ProviderOwnedNode
    {
        public int Owner { get; }
        public ProviderOwnedNode? Other { get; }
        public ProviderOwnedNode(int owner) => Owner = owner;
        public ProviderOwnedNode(ICodecProvider owner, ICodecProvider other)
        {
            Owner = 1;
            Other = OrleansGeneratedCodeHelper.GetService<ProviderOwnedNode>(this, other);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedMetadataModelComposesWithClosedFactoriesAndRollsBackCaughtExternalFailure(bool failFirst)
    {
        var services = new ServiceCollection().AddSerializer();
        var externalCalls = 0;
        var listFactoryCalls = 0;
        var intConstructions = 0;
        var attempts = 0;
        GeneratedLookupProbe? failedProbe = null;
        InvalidOperationException? caught = null;
        services.AddSingleton<ExternalDependency>(_ =>
        {
            externalCalls++;
            return new ExternalDependency();
        });
        services.AddSingleton<ListCodec<int>>(provider =>
        {
            listFactoryCalls++;
            return new ListCodec<int>(provider.GetRequiredService<IFieldCodec<int>>());
        });
        services.Configure<TypeManifestOptions>(options =>
        {
            options.AddSerializer<int>(_ =>
            {
                intConstructions++;
                return new Int32Codec();
            }, static _ => new ShallowCopier<int>());
            options.AddSerializerService<GeneratedLookupProbe>(provider =>
            {
                var probe = new GeneratedLookupProbe(provider.GetCodec<MetadataListModel>(), provider.GetDeepCopier<MetadataListModel>(),
                    provider.GetCodec<List<int>>(), provider.GetDeepCopier<List<int>>());
                if (++attempts == 1 && failFirst)
                {
                    failedProbe = probe;
                    try
                    {
                        _ = provider.Services.GetRequiredService<ExternalDependency>();
                    }
                    catch (InvalidOperationException exception)
                    {
                        caught = exception;
                    }
                }
                return probe;
            });
        });
        using var provider = services.BuildServiceProvider();
        var codecs = provider.GetRequiredService<CodecProvider>();
        var committed = codecs.GetCodec<int>();
        if (failFirst)
        {
            var failure = Assert.Throws<InvalidOperationException>(
                () => OrleansGeneratedCodeHelper.GetService<GeneratedLookupProbe>(null!, codecs));
            Assert.Same(caught, failure);
            Assert.Contains("AddSerializerService", failure.Message, StringComparison.Ordinal);
        }
        var root = OrleansGeneratedCodeHelper.GetService<GeneratedLookupProbe>(null!, codecs);
        Assert.Equal(failFirst ? 2 : 1, attempts);
        Assert.Equal(1, intConstructions);
        Assert.Equal(0, listFactoryCalls);
        Assert.Equal(0, externalCalls);
        Assert.Same(committed, codecs.GetCodec<int>());
        Assert.Same(root.Codec, codecs.GetCodec<MetadataListModel>());
        Assert.Same(root.Copier, codecs.GetDeepCopier<MetadataListModel>());
        Assert.Same(root.ListCodec, codecs.GetCodec<List<int>>());
        Assert.Same(root.ListCopier, codecs.GetDeepCopier<List<int>>());
        Assert.Same(root, OrleansGeneratedCodeHelper.GetService<GeneratedLookupProbe>(null!, codecs));
        Assert.Contains("OrleansCodeGen", root.Codec.GetType().Namespace!, StringComparison.Ordinal);
        if (failFirst)
        {
            Assert.NotNull(failedProbe);
            Assert.NotSame(failedProbe.Codec, root.Codec);
            Assert.NotSame(failedProbe.Copier, root.Copier);
            Assert.NotSame(failedProbe.ListCodec, root.ListCodec);
            Assert.NotSame(failedProbe.ListCopier, root.ListCopier);
        }
        var original = new MetadataListModel { Values = new() { 13, 17 } };
        original.Alias = original.Values;
        original.Next = original;
        original.Nested = new() { original.Values, original.Values };
        var serializer = provider.GetRequiredService<Serializer>();
        var restored = serializer.Deserialize<MetadataListModel>(serializer.SerializeToArray(original))!;
        var copied = provider.GetRequiredService<DeepCopier>().Copy(original)!;
        foreach (var result in new[] { restored, copied })
        {
            Assert.Equal(new[] { 13, 17 }, result.Values);
            Assert.Same(result.Values, result.Alias);
            Assert.Equal(2, result.Nested.Count);
            Assert.Same(result.Values, result.Nested[0]);
            Assert.Same(result.Values, result.Nested[1]);
            Assert.Same(result, result.Next);
            Assert.NotSame(original, result);
            Assert.NotSame(original.Values, result.Values);
        }
        copied.Values[0] = 23;
        Assert.Equal(13, original.Values[0]);
        Assert.Equal(23, copied.Alias[0]);
        _ = codecs.Services.GetRequiredService<ListCodec<int>>();
        _ = codecs.Services.GetRequiredService<ExternalDependency>();
        Assert.Equal(1, listFactoryCalls);
        Assert.Equal(1, externalCalls);
    }

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
    [InlineData("activator", typeof(ArgumentException))]
    [InlineData("value serializer", typeof(KeyNotFoundException))]
    [InlineData("base copier", typeof(KeyNotFoundException))]
    public void CaughtDirectMaterializationFailureRollsBackPendingGraph(string service, Type exceptionType)
    {
        var services = new ServiceCollection().AddSerializer();
        var leaves = 0;
        var attempts = 0;
        Leaf? failedLeaf = null;
        Exception? caught = null;
        services.Configure<TypeManifestOptions>(options =>
        {
            options.AddActivator(typeof(ConstrainedActivator<>));
            options.AddSerializer(typeof(ConstrainedValueSerializer<>));
            options.AddCopier(typeof(ConstrainedBaseCopier<>));
            options.AddActivator(typeof(UnboundActivator<,>), typeof(ReferenceModel<int>));
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
                    catch (Exception exception) when (exception.GetType() == exceptionType)
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

        var failure = Record.Exception(() => OrleansGeneratedCodeHelper.GetService<Root>(null!, codecs));
        Assert.IsType(exceptionType, failure);
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

    [GenerateSerializer]
    public sealed class MetadataListModel
    {
        [Id(0)]
        public List<int> Values { get; set; } = new();
        [Id(1)]
        public List<int> Alias { get; set; } = new();
        [Id(2)]
        public MetadataListModel? Next { get; set; }
        [Id(3)]
        public List<List<int>> Nested { get; set; } = new();
    }

    public sealed class ExternalDependency;
    private sealed class GeneratedLookupProbe(IFieldCodec<MetadataListModel> codec, IDeepCopier<MetadataListModel> copier,
        IFieldCodec<List<int>> listCodec, IDeepCopier<List<int>> listCopier)
    {
        public IFieldCodec<MetadataListModel> Codec { get; } = codec;
        public IDeepCopier<MetadataListModel> Copier { get; } = copier;
        public IFieldCodec<List<int>> ListCodec { get; } = listCodec;
        public IDeepCopier<List<int>> ListCopier { get; } = listCopier;
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

    public sealed class UnboundActivator<T, TUnused> : IActivator<ReferenceModel<T>>
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
