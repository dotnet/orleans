using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.Serializers;

namespace Orleans.Serialization.ContextSmoke;

public static partial class StaticFactoryContracts
{
    public static void FactoryCyclesBeforeInstanceConstructionFaultGraphAndRetry()
    {
        foreach (var mutual in new[] { false, true })
        {
            var leaves = 0;
            var attempts = 0;
            FactoryCycleLeaf? failedLeaf = null;
            InvalidOperationException? caught = null;
            var services = new ServiceCollection().AddSerializerContext(new ConstructionContext());
            services.Configure<TypeManifestOptions>(options =>
            {
                options.AddSerializerService<FactoryCycleLeaf>(_ => new FactoryCycleLeaf(++leaves));
                options.AddSerializerService<FactoryCycleDependency>(provider =>
                    new FactoryCycleDependency(OrleansGeneratedCodeHelper.GetService<FactoryCycleRoot>(null!, provider)));
                options.AddSerializerService<FactoryCycleRoot>(provider =>
                {
                    var leaf = OrleansGeneratedCodeHelper.GetService<FactoryCycleLeaf>(null!, provider);
                    if (++attempts == 1)
                    {
                        failedLeaf = leaf;
                        try
                        {
                            if (mutual) _ = OrleansGeneratedCodeHelper.GetService<FactoryCycleDependency>(null!, provider);
                            else _ = OrleansGeneratedCodeHelper.GetService<FactoryCycleRoot>(null!, provider);
                        }
                        catch (InvalidOperationException exception)
                        {
                            caught = exception;
                        }
                    }
                    return new FactoryCycleRoot(leaf);
                });
            });
            using var scope = services.BuildServiceProvider();
            var codecs = scope.GetRequiredService<CodecProvider>();
            var committed = codecs.GetCodec<int>();
            InvalidOperationException? failure = null;
            try
            {
                _ = OrleansGeneratedCodeHelper.GetService<FactoryCycleRoot>(null!, codecs);
            }
            catch (InvalidOperationException exception)
            {
                failure = exception;
            }
            Ensure(failure is not null && ReferenceEquals(failure, caught)
                && failure.Message.Contains("in-progress instance", StringComparison.Ordinal),
                "Factory cycles fault the graph with the original registration diagnostic even when caught.");
            var root = OrleansGeneratedCodeHelper.GetService<FactoryCycleRoot>(null!, codecs);
            Ensure(attempts == 2 && leaves == 2 && root.Leaf.Generation == 2
                && !ReferenceEquals(failedLeaf, root.Leaf), "Retry rebuilds every unpublished factory dependency.");
            Ensure(ReferenceEquals(root, OrleansGeneratedCodeHelper.GetService<FactoryCycleRoot>(null!, codecs))
                && ReferenceEquals(root.Leaf, OrleansGeneratedCodeHelper.GetService<FactoryCycleLeaf>(null!, codecs))
                && ReferenceEquals(committed, codecs.GetCodec<int>()), "Retry commits canonical services and preserves completed leaves.");
            Ensure(ReferenceEquals(root, OrleansGeneratedCodeHelper.GetService<FactoryCycleDependency>(null!, codecs).Root),
                "The formerly cyclic factory resolves the completed root after retry.");
        }
    }

    private sealed class FactoryCycleLeaf(int generation)
    {
        public int Generation { get; } = generation;
    }

    private sealed class FactoryCycleRoot(FactoryCycleLeaf leaf)
    {
        public FactoryCycleLeaf Leaf { get; } = leaf;
    }

    private sealed class FactoryCycleDependency(FactoryCycleRoot root)
    {
        public FactoryCycleRoot Root { get; } = root;
    }

    public static void MixedNullableTupleCyclesDeferOptionalQueries()
    {
        foreach (var tupleRoot in new[] { false, true })
        {
            var services = new ServiceCollection().AddSerializerContext(new ConstructionContext());
#if NATIVE_AOT_SMOKE
            // Both value-type forward edges are statically closed before the ordinary metadata constructors allocate their callers.
            var nullableHolderConstructions = 0;
            var valueHolderConstructions = 0;
            services.AddSingleton<Orleans.Serialization.Cloning.IDeepCopier<OptionalCycleValue?>>(serviceProvider =>
            {
                var codecs = serviceProvider.GetRequiredService<CodecProvider>();
                Ensure(!codecs.IsConstructionPending, "The closed nullable forward holder is supplied before a factory joins the ordinary metadata graph.");
                nullableHolderConstructions++;
                return ConstructionContext.CreateForwardCopier<OptionalCycleValue?>(codecs);
            });
            services.AddSingleton<Orleans.Serialization.Cloning.IDeepCopier<OptionalCycleValue>>(serviceProvider =>
            {
                var codecs = serviceProvider.GetRequiredService<CodecProvider>();
                Ensure(!codecs.IsConstructionPending, "The closed value forward holder is supplied before a factory joins the ordinary metadata graph.");
                valueHolderConstructions++;
                return ConstructionContext.CreateForwardCopier<OptionalCycleValue>(codecs);
            });
#endif
            services.Configure<TypeManifestOptions>(options =>
            {
                options.AddCopier(typeof(NullableCopier<>));
                options.AddCopier(typeof(TupleCopier<>));
                options.AddCopier(typeof(OptionalCycleCopier));
                options.AddSerializerService<OptionalCycleCopier>(provider => new OptionalCycleCopier(provider));
                options.AddSerializerService<Orleans.Serialization.Cloning.IDeepCopier<OptionalCycleValue>>(
                    static provider => OrleansGeneratedCodeHelper.GetService<OptionalCycleCopier>(null!, provider));
            });
            using var scope = services.BuildServiceProvider();
            var provider = scope.GetRequiredService<CodecProvider>();
            if (tupleRoot) _ = provider.GetDeepCopier<Tuple<OptionalCycleValue?>>();
            else _ = provider.GetDeepCopier<OptionalCycleValue>();
#if NATIVE_AOT_SMOKE
            Ensure(nullableHolderConstructions == (tupleRoot ? 1 : 0) && valueHolderConstructions == (tupleRoot ? 1 : 0),
                "Pending nullable dependencies reuse the actual constructed copier instead of entering DI.");
#endif
            var copier = OrleansGeneratedCodeHelper.GetService<OptionalCycleCopier>(null!, provider);
            Ensure(ReferenceEquals(copier.Nullable, provider.GetDeepCopier<OptionalCycleValue?>())
                && ReferenceEquals(copier.Tuple, provider.GetDeepCopier<Tuple<OptionalCycleValue?>>()),
                "Optional mixed cycles retain canonical dependencies from both roots.");
            var links = new OptionalCycleValue?[1];
            var original = new OptionalCycleValue { Values = new() { 13, 17 }, Links = links };
            links[0] = original;
            using var context = scope.GetRequiredService<Orleans.Serialization.Cloning.CopyContextPool>().GetContext();
            var result = copier.Tuple.DeepCopy(Tuple.Create<OptionalCycleValue?>(original), context)!;
            var copy = result.Item1!.Value;
            Ensure(!ReferenceEquals(original.Values, copy.Values) && !ReferenceEquals(links, copy.Links),
                "Optional cycles copy mutable members after all constructors complete.");
            Ensure(ReferenceEquals(copy.Values, copy.Links[0]!.Value.Values)
                && ReferenceEquals(copy.Links, copy.Links[0]!.Value.Links), "Optional cycles preserve shared values and self-references.");
            copy.Values[0] = 23;
            Ensure(original.Values[0] == 13 && copy.Links[0]!.Value.Values[0] == 23,
                "Optional cyclic copies isolate original values.");
        }
    }

    public struct OptionalCycleValue
    {
        public List<int> Values;
        public OptionalCycleValue?[] Links;
    }

    private sealed class OptionalCycleCopier : Orleans.Serialization.Cloning.IDeepCopier<OptionalCycleValue>,
        Orleans.Serialization.Cloning.IOptionalDeepCopier
    {
        private readonly bool _ready;
        public NullableCopier<OptionalCycleValue> Nullable { get; }
        public TupleCopier<OptionalCycleValue?> Tuple { get; }
        public OptionalCycleCopier(ICodecProvider provider)
        {
            Nullable = OrleansGeneratedCodeHelper.GetService<NullableCopier<OptionalCycleValue>>(this, provider);
            Tuple = OrleansGeneratedCodeHelper.GetService<TupleCopier<OptionalCycleValue?>>(this, provider);
            _ready = true;
        }
        public bool IsShallowCopyable()
        {
            Ensure(_ready, "No consuming constructor queries an incomplete optional copier.");
            return false;
        }
        public OptionalCycleValue DeepCopy(OptionalCycleValue input, Orleans.Serialization.Cloning.CopyContext context)
        {
            Ensure(_ready, "Optional copier operations begin after construction.");
            if (!context.TryGetCopy<List<int>>(input.Values, out var values))
            {
                values = new List<int>(input.Values);
                context.RecordCopy(input.Values, values);
            }
            if (!context.TryGetCopy<OptionalCycleValue?[]>(input.Links, out var links))
            {
                links = new OptionalCycleValue?[input.Links.Length];
                context.RecordCopy(input.Links, links);
                for (var index = 0; index < links.Length; index++)
                    links[index] = Nullable.DeepCopy(input.Links[index], context);
            }
            return new OptionalCycleValue { Values = values!, Links = links! };
        }
    }

    public static void GeneratedMixedCyclesPreserveObjectGraphsFromBothRoots()
    {
        foreach (var collectionRoot in new[] { false, true })
        {
            var services = new ServiceCollection().AddSerializerContext(new ConstructionContext());
            services.Configure<TypeManifestOptions>(options =>
            {
                options.AddSerializer(typeof(ListCodec<>));
                options.AddCopier(typeof(ListCopier<>));
                options.AddAllowedType(typeof(GeneratedMixedValue));
                options.AddSerializerService<global::OrleansCodeGen.Orleans.Serialization.ContextSmoke.StaticFactoryContracts.Codec_GeneratedMixedValue>(
                    provider => new global::OrleansCodeGen.Orleans.Serialization.ContextSmoke.StaticFactoryContracts.Codec_GeneratedMixedValue(provider));
                options.AddSerializerService<global::OrleansCodeGen.Orleans.Serialization.ContextSmoke.StaticFactoryContracts.Copier_GeneratedMixedValue>(
                    provider => new global::OrleansCodeGen.Orleans.Serialization.ContextSmoke.StaticFactoryContracts.Copier_GeneratedMixedValue(provider));
                options.AddSerializer<GeneratedMixedValue>(
                    static provider => OrleansGeneratedCodeHelper.GetService<global::OrleansCodeGen.Orleans.Serialization.ContextSmoke.StaticFactoryContracts.Codec_GeneratedMixedValue>(null!, provider),
                    static provider => OrleansGeneratedCodeHelper.GetService<global::OrleansCodeGen.Orleans.Serialization.ContextSmoke.StaticFactoryContracts.Copier_GeneratedMixedValue>(null!, provider));
            });
            using var scope = services.BuildServiceProvider();
            var provider = scope.GetRequiredService<CodecProvider>();
            if (collectionRoot)
            {
                _ = provider.GetCodec<List<GeneratedMixedValue>>();
                _ = provider.GetDeepCopier<List<GeneratedMixedValue>>();
            }
            else
            {
                _ = provider.GetCodec<GeneratedMixedValue>();
                _ = provider.GetDeepCopier<GeneratedMixedValue>();
            }
            var original = new GeneratedMixedValue { Value = 42 };
            original.Children = new() { original, original };
            original.Alias = original.Children;
            var serializer = scope.GetRequiredService<Serializer>();
            var restored = serializer.Deserialize<GeneratedMixedValue>(serializer.SerializeToArray(original))!;
            var copied = scope.GetRequiredService<DeepCopier>().Copy(original)!;
            foreach (var value in new[] { restored, copied })
            {
                Ensure(value.Value == 42 && value.Children.Count == 2, "Generated mixed cycles retain values.");
                Ensure(ReferenceEquals(value, value.Children[0]) && ReferenceEquals(value, value.Children[1])
                    && ReferenceEquals(value.Children, value.Alias), "Generated mixed cycles retain self-reference and collection aliases.");
                Ensure(!ReferenceEquals(original, value) && !ReferenceEquals(original.Children, value.Children),
                    "Generated mixed cycles isolate copied and deserialized values.");
            }
            copied.Value = 17;
            Ensure(original.Value == 42 && copied.Children[0].Value == 17, "Copied mixed cycles retain isolated canonical identities.");
        }
    }

    [GenerateSerializer]
    public sealed class GeneratedMixedValue
    {
        [Id(0)] public int Value { get; set; }
        [Id(1)] public List<GeneratedMixedValue> Children { get; set; } = new();
        [Id(2)] public List<GeneratedMixedValue> Alias { get; set; } = new();
    }

    public static void GeneratedMetadataCollectionsComposeWithClosedFactories()
    {
        var services = new ServiceCollection().AddSerializerContext(new ConstructionContext());
        services.Configure<TypeManifestOptions>(options =>
        {
            options.AddSerializer(typeof(ListCodec<>));
            options.AddCopier(typeof(ListCopier<>));
            options.AddSerializer(typeof(global::OrleansCodeGen.Orleans.Serialization.ContextSmoke.StaticFactoryContracts.Codec_MetadataCompositionValue));
            options.AddCopier(typeof(global::OrleansCodeGen.Orleans.Serialization.ContextSmoke.StaticFactoryContracts.Copier_MetadataCompositionValue));
            options.AddAllowedType(typeof(MetadataCompositionValue));
            options.AddSerializerService<MetadataCompositionRoot>(provider =>
                new MetadataCompositionRoot(provider.GetCodec<MetadataCompositionValue>(), provider.GetDeepCopier<MetadataCompositionValue>()));
        });
        using var scope = services.BuildServiceProvider();
        var codecs = scope.GetRequiredService<CodecProvider>();
        var committed = codecs.GetCodec<int>();
        var root = OrleansGeneratedCodeHelper.GetService<MetadataCompositionRoot>(null!, codecs);
        Ensure(ReferenceEquals(root.Codec, codecs.GetCodec<MetadataCompositionValue>())
            && ReferenceEquals(root.Copier, codecs.GetDeepCopier<MetadataCompositionValue>()),
            "Generated metadata implementations remain canonical after closed-factory construction.");
        Ensure(ReferenceEquals(committed, codecs.GetCodec<int>()), "Metadata composition reuses the committed closed element codec.");
        var original = new MetadataCompositionValue { Values = new() { 13, 17 } };
        original.Alias = original.Values;
        original.Nested = new() { original.Values, original.Values };
        original.Next = original;
        var serializer = scope.GetRequiredService<Serializer>();
        var restored = serializer.Deserialize<MetadataCompositionValue>(serializer.SerializeToArray(original))!;
        var copied = scope.GetRequiredService<DeepCopier>().Copy(original)!;
        foreach (var value in new[] { restored, copied })
        {
            Ensure(value.Values.Count == 2 && value.Values[0] == 13 && value.Values[1] == 17,
                "Generated metadata collections preserve element values.");
            Ensure(ReferenceEquals(value.Values, value.Alias)
                && ReferenceEquals(value.Values, value.Nested[0]) && ReferenceEquals(value.Values, value.Nested[1])
                && ReferenceEquals(value, value.Next), "Generated metadata preserves nested aliases and cycles.");
            Ensure(!ReferenceEquals(original, value) && !ReferenceEquals(original.Values, value.Values),
                "Generated metadata restores independent values.");
        }
        copied.Values[0] = 23;
        Ensure(original.Values[0] == 13 && copied.Alias[0] == 23, "Generated metadata copying isolates original collections.");
    }

    [GenerateSerializer]
    public sealed class MetadataCompositionValue
    {
        [Id(0)] public List<int> Values { get; set; } = new();
        [Id(1)] public List<int> Alias { get; set; } = new();
        [Id(2)] public List<List<int>> Nested { get; set; } = new();
        [Id(3)] public MetadataCompositionValue? Next { get; set; }
    }

    private sealed class MetadataCompositionRoot(IFieldCodec<MetadataCompositionValue> codec,
        Orleans.Serialization.Cloning.IDeepCopier<MetadataCompositionValue> copier)
    {
        public IFieldCodec<MetadataCompositionValue> Codec { get; } = codec;
        public Orleans.Serialization.Cloning.IDeepCopier<MetadataCompositionValue> Copier { get; } = copier;
    }

    public static void KeyedFacadePreservesProviderCapabilitiesOutsideConstruction()
    {
        var unkeyed = new KeyedDependency<int>();
        var keyed = new KeyedDependency<int>();
        var services = new ServiceCollection().AddSerializerContext(new ConstructionContext());
        services.AddSingleton<IKeyedDependency<int>>(unkeyed);
        services.AddKeyedSingleton<IKeyedDependency<int>>("instance", keyed);
        var constructions = 0;
        services.AddKeyedSingleton<IKeyedDependency<string>>("factory", (_, _) =>
        {
            constructions++;
            return new KeyedDependency<string>();
        });
        using var scope = services.BuildServiceProvider();
        var codecs = scope.GetRequiredService<CodecProvider>();
        var facade = codecs.Services;
        Ensure(facade is IKeyedServiceProvider, "The stable facade retains keyed resolution capability.");
        Ensure(ReferenceEquals(keyed, facade.GetRequiredKeyedService<IKeyedDependency<int>>("instance")),
            "Required keyed lookup preserves the registered instance.");
        Ensure(facade.GetKeyedService<IKeyedDependency<int>>("missing") is null, "Optional missing keyed lookup returns null outside construction.");
        Expect<InvalidOperationException>(() => facade.GetRequiredKeyedService<IKeyedDependency<int>>("missing"), "registered");
        Ensure(ReferenceEquals(unkeyed, facade.GetRequiredKeyedService<IKeyedDependency<int>>(null)),
            "A null key retains the ordinary unkeyed lookup contract.");
        var availability = facade.GetRequiredService<IServiceProviderIsKeyedService>();
        Ensure(availability.IsKeyedService(typeof(IKeyedDependency<int>), "instance")
            && availability.IsKeyedService(typeof(IKeyedDependency<string>), "factory")
            && availability.IsKeyedService(typeof(IKeyedDependency<int>), null)
            && !availability.IsKeyedService(typeof(IKeyedDependency<int>), "missing"),
            "Keyed availability retains required, optional, and null-key semantics.");
        Ensure(((IServiceProviderIsKeyedService)facade).IsKeyedService(typeof(IKeyedDependency<int>), "instance"),
            "Direct keyed availability queries preserve the provider capability.");
        var fromFactory = facade.GetRequiredKeyedService<IKeyedDependency<string>>("factory");
        Ensure(ReferenceEquals(fromFactory, facade.GetRequiredKeyedService<IKeyedDependency<string>>("factory")) && constructions == 1,
            "Outside construction, keyed singleton creation remains container-owned and cached once.");
        Ensure(ReferenceEquals(facade, codecs.Services), "The facade identity remains stable after keyed resolution.");
    }

    public static void CapturedKeyedFacadeGuardsPendingConstruction()
    {
        var services = new ServiceCollection().AddSerializerContext(new ConstructionContext());
        var instance = new KeyedDependency<int>();
        services.AddSingleton<IKeyedDependency<int>>(instance);
        var keyedConstructions = 0;
        services.AddKeyedSingleton<IKeyedDependency<int>>("instance", instance);
        services.AddKeyedSingleton<IKeyedDependency<int>>("factory", (_, _) =>
        {
            keyedConstructions++;
            return new KeyedDependency<int>();
        });
        IServiceProvider facade = null!;
        var leaves = 0;
        var attempts = 0;
        InvalidOperationException? caught = null;
        services.Configure<TypeManifestOptions>(options =>
        {
            options.AddSerializerService<KeyedConstructionLeaf>(_ => new KeyedConstructionLeaf(++leaves));
            options.AddSerializerService<IKeyedDependency<int>>(_ => instance);
            options.AddSerializerService<KeyedConstructionRoot>(provider =>
            {
                var leaf = OrleansGeneratedCodeHelper.GetService<KeyedConstructionLeaf>(null!, provider);
                var dependency = facade.GetRequiredKeyedService<IKeyedDependency<int>>(null);
                if (++attempts == 1)
                {
                    Ensure(!((IServiceProviderIsKeyedService)facade).IsKeyedService(typeof(IKeyedDependency<int>), "factory"),
                        "External keyed dependencies require an explicit closed factory during construction.");
                    try
                    {
                        _ = facade.GetRequiredKeyedService<IKeyedDependency<int>>("factory");
                    }
                    catch (InvalidOperationException error)
                    {
                        caught = error;
                    }
                }
                Ensure(ReferenceEquals(instance, dependency),
                    "A captured null-key lookup resolves the explicit closed service during construction.");
                return new KeyedConstructionRoot(leaf, dependency);
            });
        });
        using var scope = services.BuildServiceProvider();
        var codecs = scope.GetRequiredService<CodecProvider>();
        facade = codecs.Services;
        var committed = codecs.GetCodec<int>();
        InvalidOperationException? failed = null;
        try
        {
            _ = OrleansGeneratedCodeHelper.GetService<KeyedConstructionRoot>(null!, codecs);
        }
        catch (InvalidOperationException error)
        {
            failed = error;
        }
        Ensure(failed is not null && ReferenceEquals(failed, caught), "A caught keyed DI rejection faults the root with the original exception.");
        Ensure(keyedConstructions == 0 && leaves == 1, "The captured facade rejects keyed singleton construction before entering DI.");
        var rebuilt = OrleansGeneratedCodeHelper.GetService<KeyedConstructionRoot>(null!, codecs);
        Ensure(ReferenceEquals(instance, rebuilt.Dependency), "Explicit closed factories retain captured instances during construction.");
        Ensure(leaves == 2 && rebuilt.Leaf.Generation == 2
            && ReferenceEquals(rebuilt.Leaf, OrleansGeneratedCodeHelper.GetService<KeyedConstructionLeaf>(null!, codecs)),
            "Retry rebuilds and commits the canonical leaf after keyed rejection.");
        Ensure(ReferenceEquals(committed, codecs.GetCodec<int>()), "Keyed rejection preserves committed leaves.");
        _ = facade.GetRequiredKeyedService<IKeyedDependency<int>>("factory");
        Ensure(keyedConstructions == 1, "The captured facade forwards keyed factory lookup once the graph is complete.");
    }

    private sealed class KeyedConstructionLeaf(int generation)
    {
        public int Generation { get; } = generation;
    }

    private sealed class KeyedConstructionRoot(KeyedConstructionLeaf leaf, IKeyedDependency<int> dependency)
    {
        public KeyedConstructionLeaf Leaf { get; } = leaf;
        public IKeyedDependency<int> Dependency { get; } = dependency;
    }

    public static void ClosedDependenciesRemainIndependentOfKeyedDiRegistrations()
    {
        var unkeyed = new KeyedDependency<int>();
        var keyed = new KeyedDependency<int>();
        var services = new ServiceCollection().AddSerializerContext(new ConstructionContext());
        services.AddSingleton<IKeyedDependency<int>>(unkeyed);
        services.AddKeyedSingleton<IKeyedDependency<int>>("keyed", keyed);
        services.AddKeyedSingleton(typeof(IKeyedDependency<>), "open-keyed", typeof(KeyedDependency<>));
        services.Configure<TypeManifestOptions>(options =>
        {
            options.AddSerializerService<IKeyedDependency<int>>(_ => unkeyed);
            options.AddSerializerService<KeyedLookupResult>(provider =>
            {
                var dependency = provider.Services.GetRequiredService<IKeyedDependency<int>>();
                var availability = (IServiceProviderIsService)provider.Services.GetRequiredService(typeof(IServiceProviderIsService));
                Ensure(availability.IsService(typeof(IKeyedDependency<int>)), "The explicit closed service is available during graph construction.");
                Ensure(!availability.IsService(typeof(IKeyedDependency<string>)), "External keyed registrations require explicit closed construction.");
                return new KeyedLookupResult(dependency);
            });
        });
        using var scope = services.BuildServiceProvider();
        var codecs = scope.GetRequiredService<CodecProvider>();
        var result = OrleansGeneratedCodeHelper.GetService<KeyedLookupResult>(null!, codecs);
        Ensure(ReferenceEquals(result.Dependency, unkeyed), "Later keyed descriptors preserve the exact unkeyed instance.");
        Ensure(ReferenceEquals(scope.GetRequiredKeyedService<IKeyedDependency<int>>("keyed"), keyed), "Keyed registration remains independently available.");
    }

    public static void KeyedOnlyDescriptorsDoNotSelectDependencyConstructors()
    {
        var services = new ServiceCollection().AddSerializerContext(new ConstructionContext());
        services.AddKeyedSingleton<IKeyedDependency<int>>("exact-keyed", new KeyedDependency<int>());
        services.AddKeyedSingleton(typeof(IKeyedDependency<>), "open-keyed", typeof(KeyedDependency<>));
        services.Configure<TypeManifestOptions>(options =>
        {
            options.AddSerializerService<ExactKeyedConstructorProbe>(provider =>
                ActivatorUtilities.CreateInstance<ExactKeyedConstructorProbe>(provider.Services));
            options.AddSerializerService<OpenKeyedConstructorProbe>(provider =>
                ActivatorUtilities.CreateInstance<OpenKeyedConstructorProbe>(provider.Services));
        });
        using var scope = services.BuildServiceProvider();
        var codecs = scope.GetRequiredService<CodecProvider>();
        var exact = OrleansGeneratedCodeHelper.GetService<ExactKeyedConstructorProbe>(null!, codecs);
        var open = OrleansGeneratedCodeHelper.GetService<OpenKeyedConstructorProbe>(null!, codecs);
        Ensure(!exact.UsedDependency && !open.UsedDependency, "Keyed-only exact and open registrations select the parameterless constructors.");
    }

    public interface IKeyedDependency<T> { }
    public sealed class KeyedDependency<T> : IKeyedDependency<T> { }
    private sealed class KeyedLookupResult(IKeyedDependency<int> dependency)
    {
        public IKeyedDependency<int> Dependency { get; } = dependency;
    }

    public sealed class ExactKeyedConstructorProbe
    {
        public ExactKeyedConstructorProbe() { }
        public ExactKeyedConstructorProbe(IKeyedDependency<int> dependency) => UsedDependency = true;
        public bool UsedDependency { get; }
    }

    public sealed class OpenKeyedConstructorProbe
    {
        public OpenKeyedConstructorProbe() { }
        public OpenKeyedConstructorProbe(IKeyedDependency<string> dependency) => UsedDependency = true;
        public bool UsedDependency { get; }
    }

    public static void DirectProviderServicesRemainGraphOwned()
    {
        using var gate = new ConstructionGate();
        var services = new ServiceCollection().AddSerializerContext(new ConstructionContext());
        services.Configure<TypeManifestOptions>(options =>
        {
            options.AddSerializerService<ServiceProbe>(_ => new ServiceProbe(gate));
            options.AddSerializerService<IBaseCodec<FirstValue>>(static provider => OrleansGeneratedCodeHelper.GetService<ServiceProbe>(null!, provider));
            options.AddSerializerService<IValueSerializer<ProbeValue>>(static provider => OrleansGeneratedCodeHelper.GetService<ServiceProbe>(null!, provider));
            options.AddSerializerService<Orleans.Serialization.Cloning.IBaseCopier<FirstValue>>(static provider => OrleansGeneratedCodeHelper.GetService<ServiceProbe>(null!, provider));
            options.AddSerializerService<Orleans.Serialization.Activators.IActivator<FirstValue>>(static provider => OrleansGeneratedCodeHelper.GetService<ServiceProbe>(null!, provider));
            options.AddSerializerService<DirectOwner>(provider => new DirectOwner(provider, gate));
        });
        using var scope = services.BuildServiceProvider();
        var codecs = scope.GetRequiredService<CodecProvider>();
        Expect<InvalidOperationException>(() => OrleansGeneratedCodeHelper.GetService<DirectOwner>(null!, codecs), "direct owner failure");
        var root = OrleansGeneratedCodeHelper.GetService<DirectOwner>(null!, codecs);
        Ensure(root.Services.All(value => ReferenceEquals(value, root.Services[0])), "All direct base/value/copier/activator APIs resolve the same graph-owned implementation.");
        Ensure(ReferenceEquals(root.Services[0], codecs.GetActivator<FirstValue>()), "Direct service remains canonical after commit.");
        Ensure(gate.FirstConstructions == 2, "Rollback rebuilds services resolved through direct provider APIs.");
    }

#if !NATIVE_AOT_SMOKE
    public static void CaughtBaseCodecSpecializationFailureFaultsGraph()
    {
        var state = new BaseSpecializationState();
        var specializer = new FailingBaseSpecializer(state);
        var services = new ServiceCollection().AddSerializer();
        services.AddSingleton<ISpecializableBaseCodec>(specializer);
        services.Configure<TypeManifestOptions>(options =>
        {
            options.AddSerializerService<BaseSpecializationLeaf>(_ => new BaseSpecializationLeaf(state));
            options.AddSerializerService<BaseSpecializationRoot>(provider => new BaseSpecializationRoot(provider, state));
        });
        using var scope = services.BuildServiceProvider();
        var codecs = scope.GetRequiredService<CodecProvider>();
        state.Provider = codecs;
        var committed = codecs.GetCodec<int>();
        Exception? failure = null;
        try
        {
            _ = OrleansGeneratedCodeHelper.GetService<BaseSpecializationRoot>(null!, codecs);
        }
        catch (InvalidOperationException exception)
        {
            failure = exception;
        }

        Ensure(ReferenceEquals(state.Failure, failure) && ReferenceEquals(state.Failure, state.CaughtFailure),
            "Caught direct base-codec specialization failure aborts the graph with the original exception.");
        var root = OrleansGeneratedCodeHelper.GetService<BaseSpecializationRoot>(null!, codecs);
        var leaf = OrleansGeneratedCodeHelper.GetService<BaseSpecializationLeaf>(null!, codecs);
        var baseCodec = (SpecializedBaseCodec)codecs.GetBaseCodec<BaseSpecializationValue>();
        Ensure(ReferenceEquals(root.Leaf, leaf) && ReferenceEquals(baseCodec.Leaf, leaf),
            "Retry rebuilds canonical pending services and the specialized base codec.");
        Ensure(!ReferenceEquals(state.FailedLeaf, leaf), "Failed specialization publishes no pending leaf.");
        Ensure(ReferenceEquals(root, OrleansGeneratedCodeHelper.GetService<BaseSpecializationRoot>(null!, codecs)),
            "Successful retry commits one canonical root.");
        Ensure(ReferenceEquals(committed, codecs.GetCodec<int>()), "Rollback retains already committed codec leaves.");
        Ensure(state.RootConstructions == 2 && state.LeafConstructions == 2 && state.Specializations == 2,
            "Retry reconstructs the root, leaf, and specialization exactly once.");
    }

    private sealed class BaseSpecializationState
    {
        public CodecProvider Provider { get; set; } = null!;
        public InvalidOperationException Failure { get; } = new("base specialization failure");
        public Exception? CaughtFailure { get; set; }
        public BaseSpecializationLeaf? FailedLeaf { get; set; }
        public int RootConstructions;
        public int LeafConstructions;
        public int Specializations;
    }

    private sealed class BaseSpecializationLeaf
    {
        public BaseSpecializationLeaf(BaseSpecializationState state) => state.LeafConstructions++;
    }

    private sealed class BaseSpecializationRoot
    {
        public BaseSpecializationLeaf Leaf { get; }
        public BaseSpecializationRoot(ICodecProvider provider, BaseSpecializationState state)
        {
            state.RootConstructions++;
            Leaf = OrleansGeneratedCodeHelper.GetService<BaseSpecializationLeaf>(this, provider);
            try
            {
                _ = provider.GetBaseCodec<BaseSpecializationValue>();
            }
            catch (InvalidOperationException exception)
            {
                state.CaughtFailure = exception;
            }
        }
    }

    private sealed class BaseSpecializationValue { }

    private sealed class FailingBaseSpecializer(BaseSpecializationState state) : ISpecializableBaseCodec
    {
        public bool IsSupportedType(Type type) => type == typeof(BaseSpecializationValue);
        public IBaseCodec GetSpecializedCodec(Type type)
        {
            var leaf = OrleansGeneratedCodeHelper.GetService<BaseSpecializationLeaf>(this, state.Provider);
            if (++state.Specializations == 1)
            {
                state.FailedLeaf = leaf;
                throw state.Failure;
            }

            return new SpecializedBaseCodec(leaf);
        }
    }

    private sealed class SpecializedBaseCodec(BaseSpecializationLeaf leaf) : IBaseCodec<BaseSpecializationValue>
    {
        public BaseSpecializationLeaf Leaf { get; } = leaf;
        public void Serialize<TBufferWriter>(ref Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, BaseSpecializationValue value)
            where TBufferWriter : System.Buffers.IBufferWriter<byte>
        { }
        public void Deserialize<TInput>(ref Orleans.Serialization.Buffers.Reader<TInput> reader, BaseSpecializationValue value) { }
    }

    public static void SingletonFirstLookupCannotInvertGraphLock()
    {
        using var singletonStarted = new ManualResetEventSlim();
        using var releaseSingleton = new ManualResetEventSlim();
        using var ownerStarted = new ManualResetEventSlim();
        var services = new ServiceCollection().AddSerializer();
        services.AddSingleton<SingletonProbe>(provider =>
        {
            singletonStarted.Set();
            Ensure(releaseSingleton.Wait(TimeSpan.FromSeconds(10)), "Singleton-first constructor released.");
            return new SingletonProbe(OrleansGeneratedCodeHelper.GetService<DiLeaf>(null!, provider.GetRequiredService<CodecProvider>()));
        });
        services.Configure<TypeManifestOptions>(options =>
        {
            options.AddSerializerService<DiLeaf>(static _ => new DiLeaf(new ConstructionGate()));
            options.AddSerializerService<SingletonOwner>(provider =>
            {
                ownerStarted.Set();
                return new SingletonOwner(provider);
            });
        });
        using var scope = services.BuildServiceProvider();
        var codecs = scope.GetRequiredService<CodecProvider>();
        var committed = OrleansGeneratedCodeHelper.GetService<DiLeaf>(null!, codecs);
        var singleton = Task.Run(() => scope.GetRequiredService<SingletonProbe>());
        Ensure(singletonStarted.Wait(TimeSpan.FromSeconds(10)), "DI singleton constructor entered before graph owner.");
        var owner = Task.Run(() => Expect<InvalidOperationException>(
            () => OrleansGeneratedCodeHelper.GetService<SingletonOwner>(null!, codecs), "cannot resolve"));
        try
        {
            Ensure(ownerStarted.Wait(TimeSpan.FromSeconds(10)), "Graph owner started.");
            owner.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
        finally
        {
            releaseSingleton.Set();
        }
        var result = singleton.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        Ensure(ReferenceEquals(committed, result.Leaf), "Singleton uses the committed provider service after rejection releases the graph.");
    }

    public static void CaughtAutomaticAndMissingFailuresFaultGraph()
    {
        foreach (var missing in new[] { false, true })
        {
            using var gate = new ConstructionGate();
            var services = new ServiceCollection().AddSerializer();
            services.AddSingleton(gate);
            services.Configure<TypeManifestOptions>(options =>
            {
                options.AddSerializerService<DiLeaf>(_ => new DiLeaf(gate));
                options.AddSerializerService<AutomaticCatcher>(provider => new AutomaticCatcher(provider, missing));
            });
            using var scope = services.BuildServiceProvider();
            var codecs = scope.GetRequiredService<CodecProvider>();
            Expect<Exception>(() => OrleansGeneratedCodeHelper.GetService<AutomaticCatcher>(null!, codecs),
                missing ? "Could not find" : "AddSerializerService");
            var leaf = OrleansGeneratedCodeHelper.GetService<DiLeaf>(null!, codecs);
            Ensure(leaf is not null && gate.FirstConstructions == 2, "Caught automatic/missing failure discards the pending leaf.");
        }
    }

    public static void HolderCachesOnlyPublishedDependencies()
    {
        using var gate = new ConstructionGate { FailFirst = true };
        var services = new ServiceCollection().AddSerializer();
        IFieldCodec<DiLeaf>? holder = null;
        services.Configure<TypeManifestOptions>(options =>
        {
            options.AddSerializerService<DiLeaf>(_ => new DiLeaf(gate));
            options.AddSerializer<DiLeaf>(static provider => new LeafCodec(OrleansGeneratedCodeHelper.GetService<DiLeaf>(null!, provider)),
                static _ => new Orleans.Serialization.Cloning.ShallowCopier<DiLeaf>());
            options.AddSerializerService<HolderOwner>(provider => new HolderOwner(provider,
                holder ?? throw new InvalidOperationException("Holder must be resolved before owner construction."), gate));
        });
        using var scope = services.BuildServiceProvider();
        holder = scope.GetRequiredService<IFieldCodec<DiLeaf>>();
        var holderValue = holder.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Expected the singleton field codec holder's public value.");
        var codecs = scope.GetRequiredService<CodecProvider>();
        Expect<InvalidOperationException>(() => OrleansGeneratedCodeHelper.GetService<HolderOwner>(null!, codecs), "injected constructor failure");
        var owner = OrleansGeneratedCodeHelper.GetService<HolderOwner>(null!, codecs);
        var canonical = (LeafCodec)codecs.GetCodec<DiLeaf>();
        Ensure(ReferenceEquals(owner.Codec, canonical) && ReferenceEquals(holderValue.GetValue(holder), canonical), "Singleton holder re-resolves after the pending graph rolled back.");
        Ensure(gate.SecondConstructions == 2, "Holder owner retries its full construction.");
    }

    private sealed class SingletonProbe(DiLeaf leaf) { public DiLeaf Leaf { get; } = leaf; }
    private sealed class SingletonOwner
    {
        public SingletonOwner(ICodecProvider provider)
            => _ = OrleansGeneratedCodeHelper.GetService<SingletonProbe>(this, provider);
    }

    private sealed class AutomaticCatcher
    {
        public AutomaticCatcher(ICodecProvider provider, bool missing)
        {
            _ = OrleansGeneratedCodeHelper.GetService<DiLeaf>(this, provider);
            try
            {
                if (missing) _ = provider.GetCodec(typeof(UnregisteredValue));
                else _ = OrleansGeneratedCodeHelper.GetService<AutomaticFailure>(this, provider);
            }
            catch (Exception) { }
        }
    }

    private sealed class AutomaticFailure
    {
        public AutomaticFailure(ICodecProvider provider)
        {
            _ = OrleansGeneratedCodeHelper.GetService<DiLeaf>(this, provider);
            throw new InvalidOperationException("automatic constructor failure");
        }
    }
    private sealed class UnregisteredValue;

    private sealed class HolderOwner
    {
        public IFieldCodec<DiLeaf> Codec { get; }
        public HolderOwner(ICodecProvider provider, IFieldCodec<DiLeaf> holder, ConstructionGate gate)
        {
            _ = new ListCodec<DiLeaf>(holder);
            Codec = (IFieldCodec<DiLeaf>)holder.GetType().GetProperty("Value")!.GetValue(holder)!;
            Interlocked.Increment(ref gate.SecondConstructions);
            if (gate.SecondConstructions == 1) throw new InvalidOperationException("injected constructor failure");
        }
    }

    private sealed class LeafCodec(DiLeaf leaf) : IFieldCodec<DiLeaf>
    {
        public DiLeaf Leaf { get; } = leaf;
        public void WriteField<TBufferWriter>(ref Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta,
            [System.Diagnostics.CodeAnalysis.AllowNull] Type expectedType, [System.Diagnostics.CodeAnalysis.AllowNull] DiLeaf value)
            where TBufferWriter : System.Buffers.IBufferWriter<byte> => throw new NotSupportedException();
        public DiLeaf ReadValue<TInput>(ref Orleans.Serialization.Buffers.Reader<TInput> reader, Orleans.Serialization.WireProtocol.Field field)
            => throw new NotSupportedException();
    }
#endif

    private struct ProbeValue { }

    private sealed class DirectOwner
    {
        public object[] Services { get; }
        public DirectOwner(ICodecProvider provider, ConstructionGate gate)
        {
            Services = [provider.GetBaseCodec<FirstValue>(), provider.GetValueSerializer<ProbeValue>(),
                provider.GetBaseCopier<FirstValue>(), provider.GetActivator<FirstValue>()];
            if (Interlocked.Increment(ref gate.SecondConstructions) == 1)
                throw new InvalidOperationException("direct owner failure");
        }
    }

    private sealed class ServiceProbe : IBaseCodec<FirstValue>, IValueSerializer<ProbeValue>,
        Orleans.Serialization.Cloning.IBaseCopier<FirstValue>, Orleans.Serialization.Activators.IActivator<FirstValue>
    {
        public ServiceProbe(ConstructionGate gate) => Interlocked.Increment(ref gate.FirstConstructions);
        public FirstValue Create() => new();
        public void DeepCopy(FirstValue input, FirstValue output, Orleans.Serialization.Cloning.CopyContext context) { }
        public void Serialize<TBufferWriter>(ref Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, FirstValue value)
            where TBufferWriter : System.Buffers.IBufferWriter<byte> => throw new NotSupportedException();
        public void Deserialize<TInput>(ref Orleans.Serialization.Buffers.Reader<TInput> reader, FirstValue value) => throw new NotSupportedException();
        public void Serialize<TBufferWriter>(ref Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, scoped ref ProbeValue value)
            where TBufferWriter : System.Buffers.IBufferWriter<byte> => throw new NotSupportedException();
        public void Deserialize<TInput>(ref Orleans.Serialization.Buffers.Reader<TInput> reader, scoped ref ProbeValue value) => throw new NotSupportedException();
    }
}
