using Microsoft.Extensions.DependencyInjection;
using System.Buffers;
#if NET10_0_OR_GREATER
using Documentation.SerializerContexts;
#endif
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.Serializers;

namespace Orleans.Serialization.ContextSmoke;

public static partial class ContextContracts
{
    private static ServiceProvider CreateServices() => new ServiceCollection()
        .AddSerializerContext(new SmokeContext())
        .AddSerializerContext(new SmokeContext())
        .AddSerializerContext(new DuplicateContext())
        .BuildServiceProvider();

    public static void NestedCollectionsRoundTripAndCopy()
    {
        using var services = CreateServices();
        var dictionary = new Dictionary<string, int> { ["first"] = 12, ["second"] = -34 };
        var input = new List<Dictionary<string, int>> { dictionary, new(), dictionary };
        var result = RoundTrip(services, input);
        var copy = services.GetRequiredService<DeepCopier>().Copy(input);
        Ensure(result.Count == 3 && result[0].SequenceEqual(dictionary) && result[1].Count == 0, "Nested collection content/order.");
        Ensure(ReferenceEquals(result[0], result[2]), "Round-trip shared dictionary identity.");
        Ensure(copy.Count == 3 && copy[0].SequenceEqual(dictionary) && ReferenceEquals(copy[0], copy[2]), "Copied content/order/shared identity.");
        Ensure(!ReferenceEquals(input, copy) && !ReferenceEquals(input[0], copy[0]), "Deep-copy isolation.");
        copy[0]["first"] = 99;
        Ensure(input[0]["first"] == 12 && copy[2]["first"] == 99, "Mutation isolation and copy identity.");

        var valueKeys = new Dictionary<int, List<long>> { [7] = [long.MinValue, 13], [-8] = [long.MaxValue] };
        var valueResult = RoundTrip(services, valueKeys);
        Ensure(valueResult.Keys.SequenceEqual(valueKeys.Keys) && valueResult[7].SequenceEqual(valueKeys[7]), "Value-type generic instantiations.");
        var valueCopy = services.GetRequiredService<DeepCopier>().Copy(valueKeys);
        valueCopy[7].Add(42);
        Ensure(valueKeys[7].Count == 2, "Value-key collection deep-copy isolation.");
    }

    public static void GeneratedFactoriesComposeWithMetadataAndReflection()
    {
        var registrations = new ServiceCollection().AddSerializerContext(new DuplicateContext());
        var integerCodec = new UInt32Codec();
        var elementCopier = new ShallowCopier<uint>();
        var rootedArrayCodec = new ArrayCodec<uint>(integerCodec);
        var rootedArrayCopier = new ArrayCopier<uint>(elementCopier);
        registrations.AddSingleton<IFieldCodec<uint>>(integerCodec);
        registrations.AddSingleton<IDeepCopier<uint>>(elementCopier);
        registrations.Configure<TypeManifestOptions>(options =>
        {
            options.AddFieldCodec(typeof(UInt32Codec));
            options.AddCopier(typeof(ArrayCopier<uint>));
        });
        using var services = registrations.BuildServiceProvider();
        var provider = services.GetRequiredService<CodecProvider>();
        Ensure(provider.GetCodec<uint>() is UInt32Codec, "Closed metadata codecs retain reflection-based activation beside generated factories.");
        var arrayCodec = provider.GetCodec<uint[]>();
        var arrayCopier = provider.GetDeepCopier<uint[]>();
        Ensure(arrayCodec is ArrayCodec<uint> && !ReferenceEquals(rootedArrayCodec, arrayCodec)
            && ReferenceEquals(arrayCodec, provider.GetCodec<uint[]>()),
            "A statically rooted generic implementation can be materialized and activated through the common provider.");
        Ensure(arrayCopier is ArrayCopier<uint> && !ReferenceEquals(rootedArrayCopier, arrayCopier)
            && ReferenceEquals(arrayCopier, provider.GetDeepCopier<uint[]>()),
            "Closed metadata copying uses one canonical reflection-activated implementation.");
        uint[] input = [7, 11, uint.MaxValue];
        Ensure(RoundTrip(services, input).SequenceEqual(input), "Reflection-activated generic codecs preserve the wire payload.");
        var copy = services.GetRequiredService<DeepCopier>().Copy(input);
        copy[0] = 19;
        Ensure(input[0] == 7 && copy[1] == 11, "Reflection-activated generic copiers retain isolation.");
        var resolver = services.GetRequiredService<Orleans.Serialization.TypeSystem.TypeResolver>();
        Ensure(resolver is Orleans.Serialization.TypeSystem.CachedTypeResolver
            && resolver.ResolveType("System.UInt32[]") == typeof(uint[]),
            "The common type resolver resolves rooted reflection metadata beside cached generated types.");
        Ensure(services.GetRequiredService<Orleans.Serialization.TypeSystem.TypeConverter>().Parse("uint[]") == typeof(uint[]),
            "Reflection type resolution retains component validation.");
        Ensure(!resolver.TryResolveType("Missing.Serialization.Type", out _), "Unknown reflection metadata remains unresolved.");
        Expect<TypeAccessException>(() => resolver.ResolveType("Missing.Serialization.Type"), "Missing.Serialization.Type");
#if NATIVE_AOT_SMOKE
        Expect<InvalidOperationException>(() => provider.GetCodec(typeof(UnrootedValue[])), "native AOT");
#endif
    }

    private struct UnrootedValue { public int Value { get; set; } }

    public static void GeneratedModelsTraverseDependencies()
    {
        using var services = CreateServices();
        var input = new Payload<int>
        {
            Value = 42,
            Items = [new() { ["a"] = 1, ["b"] = 2 }],
            Child = new Child { Name = "nested", Numbers = [3, 5, 8] }
        };
        var result = RoundTrip(services, input);
        Ensure(result.Value == 42 && result.Child.Name == "nested" && result.Child.Numbers.SequenceEqual(input.Child.Numbers)
            && result.Items[0].SequenceEqual(input.Items[0]), "Generated model dependency traversal.");
        var copy = services.GetRequiredService<DeepCopier>().Copy(input);
        Ensure(copy.Value == input.Value && copy.Child.Name == input.Child.Name, "Generated model copy content.");
        copy.Items[0]["a"] = 77;
        copy.Child.Numbers[0] = 99;
        Ensure(input.Items[0]["a"] == 1 && input.Child.Numbers[0] == 3, "Generated model deep-copy isolation.");
        var genericValue = new ValuePayload<int> { Value = 53 };
        Ensure(RoundTrip(services, genericValue).Value == 53, "Generated generic value-type serialization.");
        Ensure(services.GetRequiredService<DeepCopier>().Copy(genericValue).Value == 53, "Generated generic value-type copying.");
    }

    public static void CanonicalValueSerializerUsesGeneratedCodec()
    {
        using var services = CreateServices();
        var provider = services.GetRequiredService<CodecProvider>();
#if NATIVE_AOT_SMOKE
        var serializer = new ValueSerializer<ValuePayload<int>>(provider, services.GetRequiredService<Orleans.Serialization.Session.SerializerSessionPool>());
#else
        var serializer = services.GetRequiredService<ValueSerializer<ValuePayload<int>>>();
#endif
        var original = new ValuePayload<int> { Value = 47 };
        var output = new ArrayBufferWriter<byte>();
        serializer.Serialize(ref original, output);
        var result = new ValuePayload<int>();
        serializer.Deserialize(output.WrittenMemory, ref result);
        Ensure(result.Value == 47, "ValueSerializer<T> serializes and deserializes using the generated struct codec.");
        Ensure(ReferenceEquals(provider.GetValueSerializer<ValuePayload<int>>(), provider.GetCodec<ValuePayload<int>>()),
            "Value and field serializer services share the same canonical generated codec.");
    }

    public static void GenericArraysRoundTripAndCopy()
    {
        using var services = CreateServices();
        var provider = services.GetRequiredService<CodecProvider>();
        var original = new Box<byte> { Values = [7, 9], Other = null! };
        original.Other = original.Values;
        var result = RoundTrip(services, original);
        var copy = services.GetRequiredService<DeepCopier>().Copy(original);
        Ensure(result.Values.SequenceEqual(original.Values) && ReferenceEquals(result.Values, result.Other),
            "Closed generic byte arrays preserve content and shared round-trip identity.");
        Ensure(!ReferenceEquals(original, copy) && !ReferenceEquals(original.Values, copy.Values)
            && ReferenceEquals(copy.Values, copy.Other), "Closed generic byte arrays preserve shared copy identity and isolation.");
        copy.Values[0] = 3;
        Ensure(original.Values[0] == 7 && copy.Other[0] == 3, "Generic byte array mutations preserve source isolation and copied identity.");
        var control = new Box<int> { Values = [13, 17], Other = [19] };
        Ensure(RoundTrip(services, control).Values.SequenceEqual(control.Values), "The generic integer array control round-trips.");
        var controlCopy = services.GetRequiredService<DeepCopier>().Copy(control);
        controlCopy.Values[0] = 23;
        Ensure(control.Values[0] == 13 && controlCopy.Other[0] == 19, "The generic integer array control copies independently.");
        Ensure(provider.GetCodec<byte[]>() is ByteArrayCodec && provider.GetDeepCopier<byte[]>() is ByteArrayCopier,
            "Canonical generic array dependencies preserve specialized direct byte-array dispatch.");
    }

    public static void GenericArrayCyclesPreserveIdentity()
    {
        using var services = CreateServices();
        var original = new GenericArrayNode { Children = new Box<GenericArrayNode>() };
        original.Children.Values = [original, original];
        original.Children.Other = original.Children.Values;
        var result = RoundTrip(services, original);
        Ensure(ReferenceEquals(result, result.Children.Values[0]) && ReferenceEquals(result, result.Children.Values[1])
            && ReferenceEquals(result.Children.Values, result.Children.Other), "Generic-array cycles preserve round-trip identity.");
        var copy = services.GetRequiredService<DeepCopier>().Copy(original);
        Ensure(!ReferenceEquals(original, copy) && !ReferenceEquals(original.Children.Values, copy.Children.Values)
            && ReferenceEquals(copy, copy.Children.Values[0]) && ReferenceEquals(copy, copy.Children.Values[1])
            && ReferenceEquals(copy.Children.Values, copy.Children.Other), "Generic-array cycles preserve copy identity and isolation.");
    }

    public static void NullableRootCyclesPreserveCopyIdentity()
    {
        using var services = new ServiceCollection().AddSerializerContext(new NullableCycleContext()).BuildServiceProvider();
        var children = new RecursiveValue?[2];
        RecursiveValue? original = new RecursiveValue { Value = 31, Children = children, Other = children };
        children[0] = original;
        children[1] = original;
        var provider = services.GetRequiredService<CodecProvider>();
        var copier = provider.GetDeepCopier<RecursiveValue?>();
        Ensure(ReferenceEquals(copier, provider.GetDeepCopier<RecursiveValue?>()), "Nullable-root resolution commits one canonical copier.");
        var copy = services.GetRequiredService<DeepCopier>().Copy(original);
        Ensure(copy.HasValue && copy.Value.Value == 31 && !ReferenceEquals(children, copy.Value.Children),
            "Nullable roots deep-copy struct content and isolate the recursive array.");
        Ensure(ReferenceEquals(copy.Value.Children, copy.Value.Other)
            && ReferenceEquals(copy.Value.Children, copy.Value.Children[0]!.Value.Children)
            && ReferenceEquals(copy.Value.Children, copy.Value.Children[1]!.Value.Other),
            "Nullable-root copies preserve shared arrays and recursive identity.");
        copy.Value.Children[0] = null;
        Ensure(children[0].HasValue && copy.Value.Other[0] is null, "Nullable-root copy mutations retain source isolation and shared identity.");
        var result = RoundTrip(services, original);
        Ensure(result.HasValue && ReferenceEquals(result.Value.Children, result.Value.Other)
            && ReferenceEquals(result.Value.Children, result.Value.Children[0]!.Value.Children),
            "Nullable-root round-trips preserve recursive array identity.");
        Ensure(services.GetRequiredService<DeepCopier>().Copy<RecursiveValue?>(null) is null, "Nullable roots preserve null values.");
    }

    public static void NullableRootFailureRollsBackAndRetriesCanonically()
    {
        var registrations = new ServiceCollection().AddSerializerContext(new NullableCycleContext());
        var attempts = 0;
        NullableCopier<RecursiveValue>? failedCopier = null;
        ArrayCopier<RecursiveValue?>? failedArray = null;
        var failure = new InvalidOperationException("nullable graph failure");
        registrations.Configure<TypeManifestOptions>(options =>
            options.AddSerializerService<NullableRetryOwner>(provider =>
            {
                var copier = OrleansGeneratedCodeHelper.GetService<NullableCopier<RecursiveValue>>(null!, provider);
                var array = OrleansGeneratedCodeHelper.GetService<ArrayCopier<RecursiveValue?>>(null!, provider);
                if (++attempts == 1)
                {
                    failedCopier = copier;
                    failedArray = array;
                    throw failure;
                }

                return new NullableRetryOwner(copier, array);
            }));
        using var services = registrations.BuildServiceProvider();
        var provider = services.GetRequiredService<CodecProvider>();
        var committed = provider.GetCodec<int>();
        Exception? observed = null;
        try { _ = OrleansGeneratedCodeHelper.GetService<NullableRetryOwner>(null!, provider); }
        catch (InvalidOperationException exception) { observed = exception; }
        Ensure(ReferenceEquals(failure, observed), "Nullable graph failure preserves the original exception.");
        var rebuilt = OrleansGeneratedCodeHelper.GetService<NullableRetryOwner>(null!, provider);
        Ensure(attempts == 2 && !ReferenceEquals(failedCopier, rebuilt.Copier) && !ReferenceEquals(failedArray, rebuilt.Array),
            "Nullable graph retry reconstructs every pending cycle service.");
        Ensure(ReferenceEquals(rebuilt.Copier, provider.GetDeepCopier<RecursiveValue?>())
            && ReferenceEquals(rebuilt.Array, provider.GetDeepCopier<RecursiveValue?[]>())
            && ReferenceEquals(rebuilt, OrleansGeneratedCodeHelper.GetService<NullableRetryOwner>(null!, provider)),
            "Nullable graph retry publishes canonical nullable, array, and root services.");
        Ensure(ReferenceEquals(committed, provider.GetCodec<int>()), "Nullable rollback preserves previously committed leaves.");
        var children = new RecursiveValue?[1];
        RecursiveValue? original = new RecursiveValue { Value = 37, Children = children, Other = children };
        children[0] = original;
        var copy = services.GetRequiredService<DeepCopier>().Copy(original);
        Ensure(copy.HasValue && !ReferenceEquals(children, copy.Value.Children)
            && ReferenceEquals(copy.Value.Children, copy.Value.Children[0]!.Value.Other),
            "Retried nullable copier preserves isolated cycle identity.");
    }

    private sealed class NullableRetryOwner(NullableCopier<RecursiveValue> copier, ArrayCopier<RecursiveValue?> array)
    {
        public NullableCopier<RecursiveValue> Copier { get; } = copier;
        public ArrayCopier<RecursiveValue?> Array { get; } = array;
    }

#if NET10_0_OR_GREATER
    public static void ReferencedGeneratedModelsTraverseDependencies()
    {
        using var services = CreateServices();
        var input = new DocumentationPayload<int> { Value = 19, Items = [new() { ["referenced"] = 23 }] };
        var result = RoundTrip(services, input);
        Ensure(result.Value == 19 && result.Items[0]["referenced"] == 23, "Referenced generated generic model dependency traversal.");
        var copy = services.GetRequiredService<DeepCopier>().Copy(input);
        copy.Items[0]["referenced"] = 37;
        Ensure(copy.Value == 19 && input.Items[0]["referenced"] == 23, "Referenced generated model deep-copy isolation.");
        Ensure(SerializerContextExample.SerializeAndCopy()[0]["count"] == 7, "Compiled documentation example outcome.");
        Ensure(RoundTrip(services, new DocumentationPrimitivePayload { Value = 67 }).Value == 67, "Referenced model uses its actual hot-reload constructor contract.");
    }
#endif

    public static void NullableArrayAndEnumRoundTrip()
    {
        using var services = CreateServices();
        int?[] input = [null, int.MinValue, 0, int.MaxValue];
        var result = RoundTrip(services, input);
        Ensure(result.SequenceEqual(input), "Nullable array round-trip.");
        var copy = services.GetRequiredService<DeepCopier>().Copy(input);
        Ensure(copy.SequenceEqual(input) && !ReferenceEquals(input, copy), "Nullable array copy.");
        Ensure(RoundTrip(services, Flavor.Second) == Flavor.Second, "Enum round-trip.");
        Ensure(services.GetRequiredService<DeepCopier>().Copy(Flavor.Second) == Flavor.Second, "Enum copy.");
        Ensure(RoundTrip(services, new List<int>()) is { Count: 0 }, "Empty collection round-trip.");
        Ensure(RoundTrip<List<int>>(services, null!) is null, "Null collection round-trip.");
        byte[] bytes = [0, 17, byte.MaxValue];
        Ensure(RoundTrip(services, bytes).SequenceEqual(bytes), "Specialized byte array round-trip.");
        var byteCopy = services.GetRequiredService<DeepCopier>().Copy(bytes);
        byteCopy[1] = 23;
        Ensure(bytes[1] == 17, "Specialized byte array deep-copy isolation.");
    }

    public static void RecursiveModelsPreserveIdentity()
    {
        using var services = CreateServices();
        var input = new Node { Value = 1 };
        input.Next = input;
        input.Other = input;
        var result = RoundTrip(services, input);
        Ensure(result.Value == 1 && ReferenceEquals(result, result.Next) && ReferenceEquals(result.Next, result.Other), "Self-cycle and shared round-trip identity.");
        var copy = services.GetRequiredService<DeepCopier>().Copy(input);
        Ensure(!ReferenceEquals(input, copy) && ReferenceEquals(copy, copy.Next) && ReferenceEquals(copy.Next, copy.Other), "Self-cycle copy identity/isolation.");
    }

    public static void MutualRecursionPreservesIdentity()
    {
        using var services = CreateServices();
        var input = new Left { Value = 11, Right = new Right { Value = 22 } };
        input.Right.Left = input;
        var result = RoundTrip(services, input);
        Ensure(result.Value == 11 && result.Right.Value == 22 && ReferenceEquals(result, result.Right.Left), "Mutual-cycle round-trip identity.");
        var copy = services.GetRequiredService<DeepCopier>().Copy(input);
        Ensure(copy.Right.Value == 22 && !ReferenceEquals(input, copy) && !ReferenceEquals(input.Right, copy.Right)
            && ReferenceEquals(copy, copy.Right.Left), "Mutual-cycle copy identity/isolation.");
    }

    public static void CollectionRecursionPreservesIdentity()
    {
        using var services = CreateServices();
        var input = new List<Branch>();
        var branch = new Branch { Children = input };
        input.Add(branch);
        input.Add(branch);
        var result = RoundTrip(services, input);
        Ensure(result.Count == 2 && ReferenceEquals(result[0], result[1]) && ReferenceEquals(result, result[0].Children), "Collection/model dependency cycle and object identity.");
        var copy = services.GetRequiredService<DeepCopier>().Copy(input);
        Ensure(!ReferenceEquals(input, copy) && !ReferenceEquals(branch, copy[0]) && ReferenceEquals(copy[0], copy[1])
            && ReferenceEquals(copy, copy[0].Children), "Collection/model copy cycle and isolation.");
    }

    public static void DuplicateContextsAndConcurrentResolution()
    {
        using var services = CreateServices();
        var provider = services.GetRequiredService<CodecProvider>();
        var codecs = new IFieldCodec[32];
        var copiers = new IDeepCopier[32];
        Parallel.For(0, codecs.Length, index =>
        {
            codecs[index] = provider.GetCodec<List<Branch>>();
            copiers[index] = provider.GetDeepCopier<List<Branch>>();
            var input = new Branch { Children = [] };
            input.Children.Add(input);
            var result = RoundTrip(services, input);
            Ensure(ReferenceEquals(result, result.Children[0]), "Concurrent operations preserve graph identity.");
        });
        Ensure(codecs.All(codec => ReferenceEquals(codec, codecs[0])) && copiers.All(copier => ReferenceEquals(copier, copiers[0])), "One canonical implementation per provider across duplicate contexts/concurrency.");
    }

    public static void MissingTypesAndCustomComparersFailClearly()
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer>();
        Ensure(!serializer.CanSerialize<HashSet<int>>(), "Unregistered types report unsupported.");
        Expect<CodecNotFoundException>(() => serializer.SerializeToArray(new HashSet<int> { 1 }), "HashSet");
        Expect<CodecNotFoundException>(() => services.GetRequiredService<DeepCopier>().Copy(new HashSet<int> { 1 }), "HashSet");
        Expect<NotSupportedException>(() => serializer.SerializeToArray(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["a"] = 1 }), "comparer");
    }

    public static void ModelAliasesAndTypeIdsRoundTrip()
    {
        using var services = CreateServices();
        var input = new List<AliasedPayload<int>> { new() { Value = 17 } };
        var result = RoundTrip(services, input);
        Ensure(result[0].Value == 17, "Generic model aliases round-trip.");
        Ensure(RoundTrip(services, new IdentifiedPayload { Value = 23 }).Value == 23, "Model type identifier round-trip.");
        Ensure(RoundTrip(services, new CompoundPayload { Value = 31 }).Value == 31, "Compound model alias round-trip.");
        Ensure(RoundTrip(services, new List<NestedAliasPayload> { new() { Value = 89 } })[0].Value == 89, "Metadata-only alias components retain their own nested aliases.");
        Ensure(RoundTrip(services, new List<MultipleAliasPayload> { new() { Value = 79 } })[0].Value == 79, "Every numeric compound alias is registered, including the formatter's lowest alias.");
        var aliases = services.GetRequiredService<Orleans.Serialization.TypeSystem.TypeConverter>();
        Ensure(aliases.Parse("(\"multiple\",\"2\")") == typeof(MultipleAliasPayload)
            && aliases.Parse("(\"multiple\",\"1\")") == typeof(MultipleAliasPayload), "Both declared compound aliases resolve to the registered model.");
        using (var separate = new ServiceCollection().AddSerializerContext(new PrefixContext())
            .AddSerializerContext(new MultipleAliasContext()).BuildServiceProvider())
        {
            Ensure(RoundTrip(separate, new List<MultipleAliasPayload> { new() { Value = 83 } })[0].Value == 83, "Multiple aliases remain registered when contexts are combined.");
        }
        foreach (var split in new[] { false, true })
        {
            var registrations = new ServiceCollection();
            if (split) registrations.AddSerializerContext(new PrefixContext()).AddSerializerContext(new ChildAliasContext());
            else registrations.AddSerializerContext(new PrefixAndChildContext());
            using var prefixes = registrations.BuildServiceProvider();
            Ensure(RoundTrip(prefixes, new List<PrefixPayload> { new() { Value = 71 } })[0].Value == 71, "Compound prefix survives traversal through intermediate alias nodes.");
            Ensure(RoundTrip(prefixes, new List<ChildAliasPayload> { new() { Value = 73 } })[0].Value == 73, "Compound child survives combined or separate contexts.");
        }
    }

#if !NATIVE_AOT_SMOKE
    public static void ExplicitContextsPreserveWireFormat()
    {
        using var context = CreateServices();
        using var legacy = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var input = new List<Dictionary<string, int>> { new() { ["a"] = 1, ["b"] = -2 } };
        var contextSerializer = context.GetRequiredService<Serializer>();
        var legacySerializer = legacy.GetRequiredService<Serializer>();
        var legacyBytes = legacySerializer.SerializeToArray(input);
        var contextBytes = contextSerializer.SerializeToArray(input);
        Ensure(contextBytes.SequenceEqual(legacyBytes), "Closed collection context preserves the existing wire format.");
        Ensure(contextSerializer.Deserialize<List<Dictionary<string, int>>>(legacyBytes)![0].SequenceEqual(input[0]), "Context reads legacy collection payloads.");
        Ensure(legacySerializer.Deserialize<List<Dictionary<string, int>>>(contextBytes)![0].SequenceEqual(input[0]), "Legacy serializer reads context collection payloads.");
        byte[] bytes = [3, 7, byte.MaxValue];
        Ensure(contextSerializer.SerializeToArray(bytes).SequenceEqual(legacySerializer.SerializeToArray(bytes)), "Closed contexts retain the specialized byte array wire format.");

        var aliased = new List<AliasedPayload<int>> { new() { Value = 29 } };
        Ensure(contextSerializer.SerializeToArray(aliased).SequenceEqual(legacySerializer.SerializeToArray(aliased)), "Closed contexts preserve generic model aliases.");
        var compound = new List<CompoundPayload> { new() { Value = 41 } };
        Ensure(contextSerializer.SerializeToArray(compound).SequenceEqual(legacySerializer.SerializeToArray(compound)), "Closed contexts preserve compound aliases.");
        var box = new Box<byte> { Values = [7, 9] };
        box.Other = box.Values;
        var boxBytes = contextSerializer.SerializeToArray(box);
        Ensure(boxBytes.SequenceEqual(legacySerializer.SerializeToArray(box)), "Closed generic array models preserve ordinary serializer wire bytes.");
        Ensure(legacySerializer.Deserialize<Box<byte>>(boxBytes)!.Values.SequenceEqual(box.Values), "Ordinary serializers read context generic array models.");
        var value = new ValuePayload<int> { Value = 47 };
        Ensure(contextSerializer.SerializeToArray(value).SequenceEqual(legacySerializer.SerializeToArray(value)), "Generated value aliases preserve ordinary serializer wire bytes.");
    }
#endif

    private static T RoundTrip<T>(IServiceProvider services, T value)
    {
        var serializer = services.GetRequiredService<Serializer>();
        return serializer.Deserialize<T>(serializer.SerializeToArray(value))!;
    }

    private static void Expect<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T exception)
        {
            Ensure(exception.Message.Contains(message, StringComparison.OrdinalIgnoreCase), $"Expected diagnostic mentioning '{message}'.");
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

[GenerateSerializerContext(typeof(List<Dictionary<string, int>>))]
[GenerateSerializerContext(typeof(Dictionary<int, List<long>>))]
[GenerateSerializerContext(typeof(Payload<int>))]
[GenerateSerializerContext(typeof(int?[]))]
[GenerateSerializerContext(typeof(byte[]))]
[GenerateSerializerContext(typeof(Flavor))]
[GenerateSerializerContext(typeof(List<int>))]
[GenerateSerializerContext(typeof(Node))]
[GenerateSerializerContext(typeof(Left))]
[GenerateSerializerContext(typeof(List<Branch>))]
[GenerateSerializerContext(typeof(List<AliasedPayload<int>>))]
[GenerateSerializerContext(typeof(IdentifiedPayload))]
[GenerateSerializerContext(typeof(List<CompoundPayload>))]
[GenerateSerializerContext(typeof(ValuePayload<int>))]
[GenerateSerializerContext(typeof(Box<byte>))]
[GenerateSerializerContext(typeof(Box<int>))]
[GenerateSerializerContext(typeof(GenericArrayNode))]
[GenerateSerializerContext(typeof(List<MultipleAliasPayload>))]
[GenerateSerializerContext(typeof(List<NestedAliasPayload>))]
#if NET10_0_OR_GREATER
[GenerateSerializerContext(typeof(DocumentationPayload<int>))]
[GenerateSerializerContext(typeof(DocumentationPrimitivePayload))]
#endif
internal partial class SmokeContext : SerializerContext;

[GenerateSerializerContext(typeof(List<Dictionary<string, int>>))]
internal partial class DuplicateContext : SerializerContext;

[GenerateSerializerContext(typeof(RecursiveValue?))]
internal partial class NullableCycleContext : SerializerContext;

[GenerateSerializer]
public struct RecursiveValue
{
    [Id(0)] public int Value { get; set; }
    [Id(1)] public RecursiveValue?[] Children { get; set; }
    [Id(2)] public RecursiveValue?[] Other { get; set; }
}

[GenerateSerializer]
public sealed class Payload<T>
{
    [Id(0)] public T Value { get; set; } = default!;
    [Id(1)] public List<Dictionary<string, int>> Items { get; set; } = [];
    [Id(2)] public Child Child { get; set; } = new();
}

[GenerateSerializer]
public sealed class Child
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public int[] Numbers { get; set; } = [];
}

[GenerateSerializer]
public enum Flavor : byte { First = 1, Second = 2 }

[GenerateSerializer]
public sealed class Node
{
    [Id(0)] public int Value { get; set; }
    [Id(1)] public Node Next { get; set; } = null!;
    [Id(2)] public Node Other { get; set; } = null!;
}

[GenerateSerializer]
public sealed class Left
{
    [Id(0)] public int Value { get; set; }
    [Id(1)] public Right Right { get; set; } = null!;
}

[GenerateSerializer]
public sealed class Right
{
    [Id(0)] public int Value { get; set; }
    [Id(1)] public Left Left { get; set; } = null!;
}

[GenerateSerializer]
public sealed class Branch
{
    [Id(0)] public List<Branch> Children { get; set; } = [];
}

[GenerateSerializer, Alias("context-payload")]
public sealed class AliasedPayload<T>
{
    [Id(0)] public T Value { get; set; } = default!;
}

[GenerateSerializer, Id(1042)]
public sealed class IdentifiedPayload
{
    [Id(0)] public int Value { get; set; }
}

[GenerateSerializer, CompoundTypeAlias(typeof(AliasMarker), "context", "payload")]
public sealed class CompoundPayload
{
    [Id(0)] public int Value { get; set; }
}

public sealed class AliasMarker;

[GenerateSerializer]
public struct ValuePayload<T>
{
    [Id(0)] public T Value { get; set; }
}

[GenerateSerializer]
public sealed class Box<T>
{
    [Id(0)] public T[] Values { get; set; } = [];
    [Id(1)] public T[] Other { get; set; } = [];
}

[GenerateSerializer]
public sealed class GenericArrayNode
{
    [Id(0)] public Box<GenericArrayNode> Children { get; set; } = null!;
}

[GenerateSerializerContext(typeof(List<PrefixPayload>))]
[GenerateSerializerContext(typeof(List<ChildAliasPayload>))]
internal partial class PrefixAndChildContext : SerializerContext;

[GenerateSerializerContext(typeof(List<PrefixPayload>))]
internal partial class PrefixContext : SerializerContext;

[GenerateSerializerContext(typeof(List<ChildAliasPayload>))]
internal partial class ChildAliasContext : SerializerContext;

[GenerateSerializer, CompoundTypeAlias("shared")]
public sealed class PrefixPayload { [Id(0)] public int Value { get; set; } }

[GenerateSerializer, CompoundTypeAlias("shared", "child")]
public sealed class ChildAliasPayload { [Id(0)] public int Value { get; set; } }

[GenerateSerializer, CompoundTypeAlias("multiple", "2"), CompoundTypeAlias("multiple", "1")]
public sealed class MultipleAliasPayload { [Id(0)] public int Value { get; set; } }

[GenerateSerializerContext(typeof(List<MultipleAliasPayload>))]
internal partial class MultipleAliasContext : SerializerContext;

[CompoundTypeAlias("metadata-marker")]
public sealed class NestedAliasMarker;

[GenerateSerializer, CompoundTypeAlias(typeof(NestedAliasMarker), "payload")]
public sealed class NestedAliasPayload { [Id(0)] public int Value { get; set; } }
