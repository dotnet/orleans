using Microsoft.Extensions.DependencyInjection;
#if NET10_0_OR_GREATER
using Documentation.SerializerContexts;
#endif
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
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
#if NET10_0_OR_GREATER
[GenerateSerializerContext(typeof(DocumentationPayload<int>))]
#endif
internal partial class SmokeContext : SerializerContext;

[GenerateSerializerContext(typeof(List<Dictionary<string, int>>))]
internal partial class DuplicateContext : SerializerContext;

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
