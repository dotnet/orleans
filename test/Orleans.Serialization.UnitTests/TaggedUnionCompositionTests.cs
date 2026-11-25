#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
using Orleans.Serialization.WireProtocol;
using Xunit;

namespace Orleans.Serialization.UnitTests.TaggedUnions;

[Trait("Category", "BVT")]
[Trait("Suite", "BVT")]
[Trait("Provider", "None")]
[Trait("Area", "Serialization")]
public sealed class TaggedUnionCompositionTests : IDisposable
{
    private readonly ServiceProvider _services = new ServiceCollection()
        .AddSingleton<NumberCallbacks>()
        .AddSingleton<ImmutablePetCallbacks>()
        .AddSingleton<RefNumberCallbacks>()
        .AddSerializer()
        .BuildServiceProvider();

    public void Dispose() => _services.Dispose();

    private T Copy<T>(T value) => _services.GetRequiredService<DeepCopier>().GetCopier<T>().Copy(value)!;
    private T RoundTrip<T>(T value)
    {
        var serializer = _services.GetRequiredService<Serializer>();
        return serializer.Deserialize<T>(serializer.SerializeToArray(value))!;
    }

    private static GraphNode CreateGraph()
    {
        var shared = new List<int> { 7, 11 };
        var root = new GraphNode { Number = 23, Values = shared };
        var child = new GraphNode { Number = 47, Values = shared, Next = root };
        root.Next = child;
        return root;
    }

    private static GraphHolder CreateHolder(GraphNode root) => new()
    {
        First = root,
        Second = root,
        Box = new GraphBox { Value = root },
        Items = [root, default, 99],
        Map = new() { ["root"] = root, ["child"] = root.Next },
        Direct = root,
    };

    private static GraphNode AssertGraph(GraphUnion value)
    {
        var root = Assert.IsType<GraphNode>(value.Value);
        var child = Assert.IsType<GraphNode>(root.Next.Value);
        Assert.Equal(23, root.Number);
        Assert.Equal(47, child.Number);
        Assert.Equal(new[] { 7, 11 }, root.Values);
        Assert.Same(root.Values, child.Values);
        Assert.Same(root, child.Next.Value);
        return root;
    }

    private static GraphNode AssertHolder(GraphHolder holder)
    {
        var root = AssertGraph(holder.First);
        Assert.Same(root, holder.Second.Value);
        Assert.Same(root, holder.Box.Value.Value);
        Assert.Equal(3, holder.Items.Count);
        Assert.Same(root, holder.Items[0].Value);
        Assert.Null(holder.Items[1].Value);
        Assert.Equal(99, holder.Items[2].Value);
        Assert.Equal(2, holder.Map.Count);
        Assert.Same(root, holder.Map["root"].Value);
        Assert.Same(root.Next.Value, holder.Map["child"].Value);
        Assert.Same(root, holder.Direct);
        return root;
    }

    [Fact]
    public void UnionGraphsRoundTripWithAliasesAndCycles()
    {
        var original = CreateGraph();
        var result = RoundTrip(CreateHolder(original));

        var root = AssertHolder(result);
        Assert.NotSame(original, root);
        Assert.NotSame(original.Next.Value, root.Next.Value);
    }

    [Fact]
    public void UnionGraphsDeepCopyWithAliasesAndCycles()
    {
        var original = CreateGraph();
        var holder = CreateHolder(original);
        var result = Copy(holder);

        var root = AssertHolder(result);
        Assert.NotSame(holder, result);
        Assert.NotSame(holder.Items, result.Items);
        Assert.NotSame(holder.Map, result.Map);
        Assert.NotSame(original, root);
        Assert.NotSame(original.Next.Value, root.Next.Value);
        Assert.NotSame(original.Values, root.Values);
        root.Values[0] = 101;
        Assert.Equal(new[] { 7, 11 }, original.Values);
        Assert.Equal(101, Assert.IsType<GraphNode>(root.Next.Value).Values[0]);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("scalar")]
    [InlineData("tracked")]
    [InlineData("suppressed")]
    [InlineData("nested")]
    [InlineData("subclass")]
    public void UnionEnvelopeReservesOneReferenceIdAndSkipKeepsAlignment(string shape)
    {
        switch (shape)
        {
            case "empty":
                Assert.Null(AssertEnvelope<GraphUnion, int>(default, 0, empty: true).Value);
                Assert.Null(AssertEnvelope<Sensor?, int>(null, 0, empty: true));
                break;
            case "scalar":
                Assert.Equal(42, AssertEnvelope<GraphUnion, int>(42, 42).Value);
                break;
            case "tracked":
                var node = new GraphNode { Number = 5, Values = [2, 3], Next = 42 };
                var read = Assert.IsType<GraphNode>(AssertEnvelope<GraphUnion, GraphNode>(node, node).Value);
                Assert.Equal(5, read.Number);
                Assert.Equal(new[] { 2, 3 }, read.Values);
                Assert.Equal(42, read.Next.Value);
                break;
            case "suppressed":
                var suppressed = new SuppressedCase { Number = 5, Values = [2, 3] };
                var readSuppressed = Assert.IsType<SuppressedCase>(AssertEnvelope<SuppressedUnion, SuppressedCase>(suppressed, suppressed).Value);
                Assert.Equal(5, readSuppressed.Number);
                Assert.Equal(new[] { 2, 3 }, readSuppressed.Values);
                break;
            case "nested":
                var light = new Light(800) { Location = "porch" };
                Assert.Equal(light, AssertEnvelope<SensorUnion, Environmental>(light, light).Value);
                Assert.Equal(light, AssertEnvelope<Sensor, Environmental>(light, light));
                break;
            case "subclass":
                var contact = new ReedContact { Open = true, Reed = 3, Location = "door" };
                Assert.Equal(contact, AssertEnvelope<SensorUnion, Contact>(contact, contact).Value);
                Assert.Equal(contact, AssertEnvelope<Sensor, Contact>(contact, contact));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape));
        }
    }

    private TUnion AssertEnvelope<TUnion, TCase>(TUnion value, TCase payload, bool empty = false)
    {
        var provider = _services.GetRequiredService<CodecProvider>();
        var pool = _services.GetRequiredService<SerializerSessionPool>();
        var codec = provider.GetCodec<TUnion>();
        var listCodec = provider.GetCodec<List<int>>();
        using var writerSession = pool.GetSession();
        var buffer = new ArrayBufferWriter<byte>();
        var writer = Writer.Create(buffer, writerSession);
        codec.WriteField(ref writer, 0, typeof(TUnion), value);
        var unionIds = writerSession.ReferencedObjects.CurrentReferenceId;

        // Compare with the case codec independently: exactly one ID belongs to the envelope.
        using var caseSession = pool.GetSession();
        var caseBuffer = new ArrayBufferWriter<byte>();
        var caseWriter = Writer.Create(caseBuffer, caseSession);
        provider.GetCodec<TCase>().WriteField(ref caseWriter, 0, typeof(TCase), payload);
        Assert.Equal(empty ? 1U : caseSession.ReferencedObjects.CurrentReferenceId + 1, unionIds);

        var trailing = new List<int> { 13, 17 };
        listCodec.WriteField(ref writer, 1, typeof(List<int>), trailing);
        listCodec.WriteField(ref writer, 1, typeof(List<int>), trailing);
        writer.Commit();

        using var readerSession = pool.GetSession();
        var reader = Reader.Create(buffer.WrittenMemory, readerSession);
        var result = codec.ReadValue(ref reader, reader.ReadFieldHeader());
        Assert.Equal(unionIds, readerSession.ReferencedObjects.CurrentReferenceId);
        var first = listCodec.ReadValue(ref reader, reader.ReadFieldHeader());
        Assert.Equal(new[] { 13, 17 }, first);
        Assert.Same(first, listCodec.ReadValue(ref reader, reader.ReadFieldHeader()));
        Assert.Equal(writer.Position, reader.Position);
        Assert.Equal(writerSession.ReferencedObjects.CurrentReferenceId, readerSession.ReferencedObjects.CurrentReferenceId);

        using var skipSession = pool.GetSession();
        var skipReader = Reader.Create(buffer.WrittenMemory, skipSession);
        skipReader.ConsumeUnknownField(skipReader.ReadFieldHeader());
        Assert.Equal(unionIds, skipSession.ReferencedObjects.CurrentReferenceId);
        var afterSkip = listCodec.ReadValue(ref skipReader, skipReader.ReadFieldHeader());
        Assert.Equal(new[] { 13, 17 }, afterSkip);
        Assert.Same(afterSkip, listCodec.ReadValue(ref skipReader, skipReader.ReadFieldHeader()));
        Assert.Equal(writer.Position, skipReader.Position);
        Assert.Equal(writerSession.ReferencedObjects.CurrentReferenceId, skipSession.ReferencedObjects.CurrentReferenceId);
        return result!;
    }

    [Fact]
    public void UnionReadersSkipAdditionalEnvelopeFieldsAndResolveTheirReferences()
    {
        Assert.Equal(42, ReadWithAdditionalEnvelopeField<GraphUnion, int>(42, 1).Value);
        Assert.Equal(new Temperature(21.5), ReadWithAdditionalEnvelopeField<Sensor, Temperature>(new Temperature(21.5), 0));
    }

    private TUnion ReadWithAdditionalEnvelopeField<TUnion, TCase>(TCase payload, uint caseId)
    {
        var provider = _services.GetRequiredService<CodecProvider>();
        var pool = _services.GetRequiredService<SerializerSessionPool>();
        var listCodec = provider.GetCodec<List<int>>();
        using var writerSession = pool.GetSession();
        var buffer = new ArrayBufferWriter<byte>();
        var writer = Writer.Create(buffer, writerSession);
        writer.WriteStartUnion(0, typeof(TUnion), typeof(TUnion));
        provider.GetCodec<TCase>().WriteField(ref writer, caseId, typeof(TCase), payload);
        var hidden = new List<int> { 13, 17 };
        // Additional content is consumed after reading the case, and can own references used outside the envelope.
        listCodec.WriteField(ref writer, 1, typeof(List<int>), hidden);
        writer.WriteEndObject();
        listCodec.WriteField(ref writer, 1, typeof(List<int>), hidden);
        listCodec.WriteField(ref writer, 1, typeof(List<int>), hidden);
        writer.Commit();

        using var readerSession = pool.GetSession();
        var reader = Reader.Create(buffer.WrittenMemory, readerSession);
        var result = provider.GetCodec<TUnion>().ReadValue(ref reader, reader.ReadFieldHeader());
        var first = listCodec.ReadValue(ref reader, reader.ReadFieldHeader());
        Assert.Equal(new[] { 13, 17 }, first);
        Assert.Same(first, listCodec.ReadValue(ref reader, reader.ReadFieldHeader()));
        Assert.Equal(writer.Position, reader.Position);
        Assert.Equal(writerSession.ReferencedObjects.CurrentReferenceId, readerSession.ReferencedObjects.CurrentReferenceId);
        return result!;
    }

    [Fact]
    public void UnionReadersRejectNonDelimitedEnvelope()
    {
        var serializer = _services.GetRequiredService<Serializer>();
        var bytes = serializer.SerializeToArray(42);

        Assert.Throws<UnsupportedWireTypeException>(() => serializer.Deserialize<GraphUnion>(bytes));
        Assert.Throws<UnsupportedWireTypeException>(() => serializer.Deserialize<Sensor>(bytes));
    }

    [Fact]
    public void UnionReadersRejectLegacyValuesOutsideTheirCases()
    {
        var serializer = _services.GetRequiredService<Serializer>();
        var bytes = serializer.SerializeToArray<object>(new Cat("not a graph case"));

        Assert.Throws<SerializerException>(() => serializer.Deserialize<GraphUnion>(bytes));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuppressedCaseReferencesRemainUntrackedButMembersKeepAliases(bool copying)
    {
        var original = new SuppressedCase { Number = 5, Values = [2, 3] };
        var values = new List<SuppressedUnion> { original, original };
        var result = copying ? Copy(values) : RoundTrip(values);

        Assert.Equal(2, result.Count);
        var first = Assert.IsType<SuppressedCase>(result[0].Value);
        var second = Assert.IsType<SuppressedCase>(result[1].Value);
        Assert.Equal(5, first.Number);
        Assert.Equal(5, second.Number);
        Assert.Equal(new[] { 2, 3 }, first.Values);
        Assert.NotSame(original, first);
        Assert.NotSame(first, second);
        Assert.NotSame(original.Values, first.Values);
        Assert.Same(first.Values, second.Values);
    }

    [Fact]
    public void GenericUnionAndClosedCasesCopySharedMutableDescendants()
    {
        var items = new List<int> { 7, 11 };
        var value = new Value<List<int>>(items);
        var input = new GenericCopyHolder<List<int>>
        {
            First = value,
            Second = value,
            Closed = new Circle<List<int>>(items),
            Immutable = value,
        };

        var result = Copy(input);
        var copied = Assert.IsType<Value<List<int>>>(result.First.Value);
        var closed = Assert.IsType<Circle<List<int>>>(result.Closed);
        Assert.NotSame(input, result);
        Assert.NotSame(value, copied);
        Assert.NotSame(input.Closed, closed);
        Assert.Equal(new[] { 7, 11 }, copied.Item);
        Assert.NotSame(items, copied.Item);
        Assert.Same(copied, result.Second.Value);
        Assert.Same(copied.Item, closed.Radius);
        Assert.Same(value, result.Immutable.Value);
        copied.Item.Add(19);
        Assert.Equal(new[] { 7, 11 }, items);
        Assert.Equal(new[] { 7, 11, 19 }, closed.Radius);

        var rightValue = new Value<string>("right");
        var right = Copy<Either<List<int>, string>>(rightValue);
        Assert.Equal("right", Assert.IsType<Value<string>>(right.Value).Item);
        Assert.NotSame(rightValue, right.Value);
        var rightMutable = Assert.IsType<Value<List<int>>>(Copy<Either<string, List<int>>>(value).Value);
        Assert.NotSame(value, rightMutable);
        Assert.NotSame(items, rightMutable.Item);
        Assert.Equal(new[] { 7, 11 }, rightMutable.Item);
        // Equal generic arguments collapse to the first case, but must not collapse the copy boundary.
        var equalInput = CreateLeft<List<int>, List<int>>(value);
        var equalArguments = Copy(new List<Either<List<int>, List<int>>> { equalInput, equalInput });
        var equalCase = Assert.IsType<Value<List<int>>>(equalArguments[0].Value);
        Assert.NotSame(value, equalCase);
        Assert.NotSame(items, equalCase.Item);
        Assert.Equal(new[] { 7, 11 }, equalCase.Item);
        Assert.Same(equalCase, equalArguments[1].Value);
        Assert.Null(Copy(default(Either<List<int>, string>)).Value);
        Assert.Null(Copy<Shape<List<int>>?>(null));
    }

    private static Either<L, R> CreateLeft<L, R>(Value<L> value) => value;

    [Fact]
    public void NestedGenericUnionCopiesMutableCaseGraph()
    {
        var root = CreateGraph();
        Either<GraphUnion, string> input = new Value<GraphUnion>(root);

        var result = Copy(new List<Either<GraphUnion, string>> { input, input });

        Assert.Equal(2, result.Count);
        var wrapper = Assert.IsType<Value<GraphUnion>>(result[0].Value);
        Assert.NotSame(input.Value, wrapper);
        Assert.Same(wrapper, result[1].Value);
        var copied = AssertGraph(wrapper.Item);
        Assert.NotSame(root, copied);
        Assert.NotSame(root.Values, copied.Values);
        copied.Values[0] = 101;
        Assert.Equal(new[] { 7, 11 }, root.Values);
    }

    [Fact]
    public void MixedUnionCopySharesOnlyExplicitlyImmutableCases()
    {
        var root = CreateGraph();
        var immutable = new ImmutableGraphCase { Values = [31, 37] };
        var result = Copy(new List<GraphUnion> { root, immutable, immutable, 99, default });

        Assert.Equal(5, result.Count);
        var copied = AssertGraph(result[0]);
        Assert.NotSame(root, copied);
        Assert.NotSame(root.Values, copied.Values);
        Assert.Same(immutable, result[1].Value);
        Assert.Same(immutable, result[2].Value);
        Assert.Equal(new[] { 31, 37 }, Assert.IsType<ImmutableGraphCase>(result[1].Value).Values);
        Assert.Equal(99, result[3].Value);
        Assert.Null(result[4].Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedAndDerivedClosedCasesPreserveMutableMembers(bool copying)
    {
        var items = new List<int> { 7, 11 };
        var light = new MutableLight(items) { Location = "porch" };
        var contact = new MutableContact { Open = true, Location = "door", Values = items };
        var input = (new List<Sensor> { light, contact }, new List<SensorUnion> { light, contact });

        var result = copying ? Copy(input) : RoundTrip(input);

        var readLight = Assert.IsType<MutableLight>(result.Item1[0]);
        var readContact = Assert.IsType<MutableContact>(result.Item1[1]);
        Assert.Equal("porch", readLight.Location);
        Assert.Equal("door", readContact.Location);
        Assert.True(readContact.Open);
        Assert.Equal(new[] { 7, 11 }, readLight.Values);
        Assert.NotSame(light, readLight);
        Assert.NotSame(contact, readContact);
        Assert.NotSame(items, readLight.Values);
        Assert.Same(readLight, result.Item2[0].Value);
        Assert.Same(readContact, result.Item2[1].Value);
        Assert.Same(readLight.Values, readContact.Values);
        readContact.Values[0] = 101;
        Assert.Equal(new[] { 7, 11 }, items);
    }

    [Theory]
    [InlineData("number")]
    [InlineData("text")]
    [InlineData("empty")]
    public void CustomUnionCopierComposesThroughContainers(string shape)
    {
        CustomNumber value = shape switch { "number" => 17, "text" => "custom", _ => default };
        object? expected = shape switch { "number" => 117, "text" => "custom copied", _ => null };
        var result = Copy(new CustomCopyHolder
        {
            Value = value,
            Box = new CustomCopyBox { Value = value },
            Items = [value],
        });

        Assert.Equal(expected, Copy(value).Value);
        Assert.Equal(expected, result.Value.Value);
        Assert.Equal(expected, result.Box.Value.Value);
        Assert.Equal(expected, Assert.Single(result.Items).Value);
        Assert.Equal(shape == "number" ? (object)17 : shape == "text" ? "custom" : null, value.Value);
    }

    [Fact]
    public void CustomUnionCodecComposesWithReferences()
    {
        var text = new string(['c', 'a', 's', 'e']);
        var input = new CustomCopyHolder
        {
            Value = text,
            Box = new CustomCopyBox { Value = 17 },
            Items = [text, default, 23],
            Trailing = [13, 17],
        };
        input.TrailingAlias = input.Trailing;

        var result = RoundTrip(input);

        Assert.Equal("case", result.Value.Value);
        Assert.Equal(17, result.Box.Value.Value);
        Assert.Equal(3, result.Items.Count);
        Assert.Same(result.Value.Value, result.Items[0].Value);
        Assert.Null(result.Items[1].Value);
        Assert.Equal(23, result.Items[2].Value);
        Assert.Equal(new[] { 13, 17 }, result.Trailing);
        Assert.Same(result.Trailing, result.TrailingAlias);

        // An older reader skips the converter-backed union, but later fields still reference its string payload.
        var serializer = _services.GetRequiredService<Serializer>();
        var skipped = serializer.Deserialize<CustomCopyHolderWithoutValue>(serializer.SerializeToArray(input))!;
        Assert.Equal(3, skipped.Items.Count);
        Assert.Equal("case", skipped.Items[0].Value);
        Assert.Null(skipped.Items[1].Value);
        Assert.Equal(23, skipped.Items[2].Value);
        Assert.Equal(new[] { 13, 17 }, skipped.Trailing);
        Assert.Same(skipped.Trailing, skipped.TrailingAlias);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImmutableUnionCallbacksComposeThroughContainers(bool empty)
    {
        ImmutableCallbackPet value = empty ? default(ImmutableCallbackPet) : new Value<List<int>>([7, 11]);
        var callbacks = _services.GetRequiredService<ImmutablePetCallbacks>();
        var input = (value, new Value<ImmutableCallbackPet>(value), new List<ImmutableCallbackPet> { value });

        var result = Copy(input);

        Assert.Same(value.Value, result.Item1.Value);
        Assert.Same(value.Value, result.Item2.Item.Value);
        Assert.Same(value.Value, Assert.Single(result.Item3).Value);
        Assert.NotSame(input.Item2, result.Item2);
        Assert.NotSame(input.Item3, result.Item3);
        Assert.Equal(new[]
        {
            ("copying", (object?)null), ("copied", value.Value),
            ("copying", (object?)null), ("copied", value.Value),
            ("copying", (object?)null), ("copied", value.Value),
        }, callbacks.Events);
    }

    [Fact]
    public void RefCopyCallbacksComposeThroughContainers()
    {
        var input = (Value: (RefCallbackNumber)10, Box: new RefCopyBox { Value = 20 }, Items: new List<RefCallbackNumber> { 30 });

        var result = Copy(input);

        Assert.Equal(12, result.Value.Value);
        Assert.Equal(22, result.Box.Value.Value);
        Assert.Equal(32, Assert.Single(result.Items).Value);
        Assert.Equal(10, input.Value.Value);
        Assert.Equal(20, input.Box.Value.Value);
        Assert.Equal(30, input.Items[0].Value);
        Assert.Equal(new (string, object?, object?)[]
        {
            ("before", 10, null), ("after", 11, 11),
            ("before", 20, null), ("after", 21, 21),
            ("before", 30, null), ("after", 31, 31),
        }, _services.GetRequiredService<RefNumberCallbacks>().Events);
    }

    [Fact]
    public void UnionCopyPolicyComposesThroughMethodParameters()
    {
        var root = CreateGraph();
        var immutable = new OpaqueCase("shared");
        var proxy = new OrleansCodeGen.Orleans.Serialization.UnitTests.TaggedUnions.Proxy_taggedunioncopyproxy(
            _services.GetRequiredService<CopyContextPool>(), _services.GetRequiredService<CodecProvider>());
        IInvokable? request = null;
        proxy.OnInvoke = value => request = value;

        _ = ((ITaggedUnionCopyProxy)proxy).Send(root, new GraphBox { Value = root }, [root], "shallow", immutable, 42, 17);

        AssertRequestPolicy(request!, root, immutable, 117);
        // The generated invokable copier is another consumer of method-parameter classification, distinct from the proxy.
        var copiedRequest = Copy(request!);
        AssertRequestPolicy(copiedRequest, Assert.IsType<GraphNode>(Assert.IsType<GraphUnion>(request!.GetArgument(0)).Value), immutable, 217);
        Assert.NotSame(request, copiedRequest);
    }

    private static void AssertRequestPolicy(IInvokable request, GraphNode original, OpaqueCase immutable, int custom)
    {
        var root = AssertGraph(Assert.IsType<GraphUnion>(request.GetArgument(0)));
        Assert.NotSame(original, root);
        Assert.NotSame(original.Values, root.Values);
        Assert.Same(root, Assert.IsType<GraphBox>(request.GetArgument(1)).Value.Value);
        Assert.Same(root, Assert.Single(Assert.IsType<List<GraphUnion>>(request.GetArgument(2))).Value);
        Assert.Equal("shallow", Assert.IsType<IntOrString>(request.GetArgument(3)).Value);
        Assert.Same(immutable, Assert.IsType<ImmutablePet>(request.GetArgument(4)).Value);
        Assert.Equal(43, Assert.IsType<CallbackNumber>(request.GetArgument(5)).Value);
        Assert.Equal(custom, Assert.IsType<CustomNumber>(request.GetArgument(6)).Value);
    }

    [Fact]
    public void GenericUnionCopyPolicyComposesThroughMethodParameters()
    {
        var items = new List<int> { 7, 11 };
        Either<List<int>, string> input = new Value<List<int>>(items);
        var proxy = new OrleansCodeGen.Orleans.Serialization.UnitTests.TaggedUnions.Proxy_taggedunioncopyproxy(
            _services.GetRequiredService<CopyContextPool>(), _services.GetRequiredService<CodecProvider>());
        IInvokable? request = null;
        proxy.OnInvoke = value => request = value;

        _ = ((ITaggedUnionCopyProxy)proxy).SendGeneric(input, [input]);

        var copied = Assert.IsType<Value<List<int>>>(Assert.IsType<Either<List<int>, string>>(request!.GetArgument(0)).Value);
        Assert.NotSame(input.Value, copied);
        Assert.NotSame(items, copied.Item);
        Assert.Equal(new[] { 7, 11 }, copied.Item);
        Assert.Same(copied, Assert.Single(Assert.IsType<List<Either<List<int>, string>>>(request.GetArgument(1))).Value);
        copied.Item[0] = 101;
        Assert.Equal(new[] { 7, 11 }, items);
    }

    [Fact]
    public void UnionMemberAndParameterImmutabilityPreserveDeepCaseReference()
    {
        var root = CreateGraph();
        var input = new Value<ImmutableGraphBox>(new ImmutableGraphBox { Value = root });

        var result = Copy(input);

        Assert.NotSame(input, result);
        Assert.Same(root, result.Item.Value.Value);
        var proxy = new OrleansCodeGen.Orleans.Serialization.UnitTests.TaggedUnions.Proxy_taggedunioncopyproxy(
            _services.GetRequiredService<CopyContextPool>(), _services.GetRequiredService<CodecProvider>());
        IInvokable? request = null;
        proxy.OnInvoke = value => request = value;
        _ = ((ITaggedUnionCopyProxy)proxy).SendImmutable(root);
        Assert.Same(root, Assert.IsType<GraphUnion>(request!.GetArgument(0)).Value);
        var copiedRequest = Copy(request!);
        Assert.NotSame(request, copiedRequest);
        Assert.Same(root, Assert.IsType<GraphUnion>(copiedRequest.GetArgument(0)).Value);
    }
}

[GenerateSerializer]
public union GraphUnion(GraphNode, int, ImmutableGraphCase);

[Immutable, GenerateSerializer]
public sealed class ImmutableGraphCase
{
    [Id(0)] public List<int> Values { get; set; } = [];
}

[GenerateSerializer]
public sealed class GraphNode
{
    [Id(0)] public int Number { get; set; }
    [Id(1)] public List<int> Values { get; set; } = [];
    [Id(2)] public GraphUnion Next { get; set; }
}

[GenerateSerializer]
public struct GraphBox
{
    [Id(0)] public GraphUnion Value { get; set; }
}

[GenerateSerializer]
public struct ImmutableGraphBox
{
    [Id(0), Immutable] public GraphUnion Value { get; set; }
}

[GenerateSerializer]
public sealed class GraphHolder
{
    [Id(0)] public GraphUnion First { get; set; }
    [Id(1)] public GraphUnion Second { get; set; }
    [Id(2)] public GraphBox Box { get; set; }
    [Id(3)] public List<GraphUnion> Items { get; set; } = [];
    [Id(4)] public Dictionary<string, GraphUnion> Map { get; set; } = [];
    [Id(5)] public GraphNode? Direct { get; set; }
}

[GenerateSerializer, SuppressReferenceTracking]
public sealed class SuppressedCase
{
    [Id(0)] public int Number { get; set; }
    [Id(1)] public List<int> Values { get; set; } = [];
}

[GenerateSerializer]
public union SuppressedUnion(SuppressedCase, int);

[GenerateSerializer]
public sealed class GenericCopyHolder<T>
{
    [Id(0)] public Either<T, string> First { get; set; }
    [Id(1)] public Either<T, string> Second { get; set; }
    [Id(2)] public Shape<T>? Closed { get; set; }
    [Id(3)] public ImmutableValue<T> Immutable { get; set; }
}

[GenerateSerializer, Case(3)]
public sealed record MutableLight([property: Id(0)] List<int> Values) : Environmental;

[GenerateSerializer]
public sealed record MutableContact : Contact
{
    [Id(0)] public List<int> Values { get; set; } = [];
}

// No GenerateSerializer: callers must respect the registered copier, even though both cases are shallow.
public union CustomNumber(int, string);

[RegisterCopier]
public sealed class CustomNumberCopier : IDeepCopier<CustomNumber>
{
    public CustomNumber DeepCopy(CustomNumber input, CopyContext context) => input.Value switch
    {
        int number => number + 100,
        string text => text + " copied",
        _ => default,
    };
}

[GenerateSerializer]
public struct CustomNumberSurrogate
{
    [Id(0)] public int? Number { get; set; }
    [Id(1)] public string? Text { get; set; }
}

[RegisterConverter]
public sealed class CustomNumberConverter : IConverter<CustomNumber, CustomNumberSurrogate>
{
    public CustomNumberSurrogate ConvertToSurrogate(in CustomNumber value) => value.Value switch
    {
        int number => new() { Number = number },
        string text => new() { Text = text },
        _ => default,
    };

    public CustomNumber ConvertFromSurrogate(in CustomNumberSurrogate value) => value.Number is { } number
        ? number : value.Text is { } text ? text : default;
}

[GenerateSerializer]
public struct CustomCopyBox
{
    [Id(0)] public CustomNumber Value { get; set; }
}

[GenerateSerializer]
public sealed class CustomCopyHolder
{
    [Id(0)] public CustomNumber Value { get; set; }
    [Id(1)] public CustomCopyBox Box { get; set; }
    [Id(2)] public List<CustomNumber> Items { get; set; } = [];
    [Id(3)] public List<int>? Trailing { get; set; }
    [Id(4)] public List<int>? TrailingAlias { get; set; }
}

[GenerateSerializer]
public sealed class CustomCopyHolderWithoutValue
{
    [Id(2)] public List<CustomNumber> Items { get; set; } = [];
    [Id(3)] public List<int>? Trailing { get; set; }
    [Id(4)] public List<int>? TrailingAlias { get; set; }
}

[GenerateSerializer, SerializationCallbacks(typeof(RefNumberCallbacks))]
public union RefCallbackNumber(int, string);

[GenerateSerializer]
public struct RefCopyBox
{
    [Id(0)] public RefCallbackNumber Value { get; set; }
}

public sealed class RefNumberCallbacks
{
    public List<(string Event, object? Original, object? Result)> Events { get; } = [];

    public void OnCopying(ref RefCallbackNumber original, ref RefCallbackNumber result)
    {
        Events.Add(("before", original.Value, result.Value));
        original = (int)original.Value! + 1;
        result = -1;
    }

    public void OnCopied(ref RefCallbackNumber original, ref RefCallbackNumber result)
    {
        Events.Add(("after", original.Value, result.Value));
        result = (int)result.Value! + 1;
    }
}

[Alias("tagged-union-copy-proxy"), GenerateMethodSerializers(typeof(MyInvokableProxyBase))]
public interface ITaggedUnionCopyProxy
{
    ValueTask Send(GraphUnion first, GraphBox box, List<GraphUnion> values, IntOrString shallow, ImmutablePet immutable, CallbackNumber callback, CustomNumber custom);
    ValueTask SendGeneric<T>(Either<T, string> first, List<Either<T, string>> others);
    ValueTask SendImmutable([Immutable] GraphUnion value);
}
