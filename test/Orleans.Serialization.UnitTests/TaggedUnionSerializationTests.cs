#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Session;
using Orleans.Serialization.Utilities;
using Xunit;
using static VerifyXunit.Verifier;

namespace Orleans.Serialization.UnitTests.TaggedUnions;

/// <summary>
/// Tests tagged-union serialization of closed type hierarchies and union types.
/// </summary>
[Trait("Category", "BVT")]
[Trait("Suite", "BVT")]
[Trait("Provider", "None")]
[Trait("Area", "Serialization")]
public class TaggedUnionSerializationTests : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly Serializer _serializer;
    private readonly DeepCopier _copier;

    public TaggedUnionSerializationTests()
    {
        _services = new ServiceCollection()
            .AddSingleton<PetCallbacks>()
            .AddSingleton<NumberCallbacks>()
            .AddSingleton<CaseOrderCallbacks>()
            .AddSingleton<CaseCallbacks>()
            .AddSingleton<ImmutablePetCallbacks>()
            .AddSerializer()
            .BuildServiceProvider();
        _serializer = _services.GetRequiredService<Serializer>();
        _copier = _services.GetRequiredService<DeepCopier>();
    }

    public void Dispose() => _services.Dispose();

    private T RoundTrip<T>(T value) => _serializer.Deserialize<T>(_serializer.SerializeToArray(value))!;

    private T Copy<T>(T value) => _copier.GetCopier<T>().Copy(value)!;

    private static readonly Sensor[] SensorValues =
    [
        new Temperature(21.5) { Location = "kitchen" },
        new Humidity(40),
        new Contact { Open = true },
        new ReedContact { Open = false, Reed = 3 },
        new Light(800) { Location = "porch" },
    ];

    public static TheoryData<Sensor> Sensors() => [.. SensorValues];

    [Theory]
    [MemberData(nameof(Sensors))]
    public void ClosedHierarchyRoundTripsThroughBaseType(Sensor value)
    {
        var result = RoundTrip<Sensor>(value);

        Assert.Equal(value.GetType(), result.GetType());
        Assert.Equal(value, result);
    }

    [Theory]
    [MemberData(nameof(Sensors))]
    public void ClosedHierarchyIsDeepCopied(Sensor value)
    {
        var result = Copy<Sensor>(value);

        Assert.NotSame(value, result);
        Assert.Equal(value, result);
    }

    [Fact]
    public void ClosedHierarchyCaseIsEncodedAsCaseIdentifier()
    {
        var value = new Temperature(21.5) { Location = "kitchen" };
        var asCase = _serializer.SerializeToArray(value);
        var asClosedType = _serializer.SerializeToArray<Sensor>(value);
        var asObject = _serializer.SerializeToArray<object>(value);

        // The union field adds a header byte and an end byte around the case field, which is encoded with the expected-type optimization.
        Assert.Equal(asCase.Length + 2, asClosedType.Length);
        Assert.True(asObject.Length > asClosedType.Length + 20, $"Expected the encoded type name to dominate: {asObject.Length} vs {asClosedType.Length}");
    }

    [Fact]
    public Task ClosedHierarchyAndEquivalentUnionEncoding_MatchesSnapshot()
    {
        // The snapshot records the closed hierarchy's encoding, and the union with the same cases and case identifiers must produce the same bytes.
        var snapshot = new StringBuilder();
        foreach (var value in SensorValues.Append<Sensor?>(null))
        {
            SensorUnion union = value switch
            {
                null => default,
                Temperature temperature => temperature,
                Humidity humidity => humidity,
                Environmental environmental => environmental,
                Contact contact => contact,
            };

            var closedBytes = _serializer.SerializeToArray<Sensor?>(value);
            var unionBytes = _serializer.SerializeToArray(union);

            Assert.Equal(closedBytes, unionBytes);
            Assert.Equal(value, _serializer.Deserialize<SensorUnion>(closedBytes).Value);
            Assert.Equal(value, _serializer.Deserialize<Sensor?>(unionBytes));
            AppendPayload(snapshot, value?.GetType().Name ?? "null", closedBytes);
        }

        return Verify(snapshot.ToString(), extension: "txt").UseDirectory("snapshots");
    }

    [Fact]
    public Task TransparentClosedHierarchyReplacedByUnionEncoding_MatchesSnapshot()
    {
        // Before the upgrade: a stateless, serializer-transparent closed base. After: a union of the same records without the base.
        var snapshot = new StringBuilder();
        foreach (var (before, after) in new (ShapeV1?, ShapeV2)[] { (new CircleV1(1.5), new CircleV2(1.5)), (new SquareV1(2), new SquareV2(2)), (null, default) })
        {
            var beforeBytes = _serializer.SerializeToArray(before);

            Assert.Equal(beforeBytes, _serializer.SerializeToArray(after));
            Assert.Equal(after, _serializer.Deserialize<ShapeV2>(beforeBytes));
            Assert.Equal(before, _serializer.Deserialize<ShapeV1?>(beforeBytes));
            AppendPayload(snapshot, before?.GetType().Name ?? "null", beforeBytes);
        }

        return Verify(snapshot.ToString(), extension: "txt").UseDirectory("snapshots");
    }

    [Fact]
    public Task UnionEncoding_MatchesSnapshot()
    {
        var snapshot = new StringBuilder();
        AppendPayload(snapshot, "Pet: Cat", _serializer.SerializeToArray<Pet>(new Cat("Whiskers")));
        AppendPayload(snapshot, "Pet: Dog", _serializer.SerializeToArray<Pet>(new Dog("Rex")));
        AppendPayload(snapshot, "Pet: empty", _serializer.SerializeToArray(default(Pet)));
        AppendPayload(snapshot, "PetV2: Dog (case 2)", _serializer.SerializeToArray<PetV2>(new Dog("Fido")));
        AppendPayload(snapshot, "IntOrString: int", _serializer.SerializeToArray<IntOrString>(42));
        AppendPayload(snapshot, "IntOrString: string", _serializer.SerializeToArray<IntOrString>("text"));
        AppendPayload(snapshot, "MaybeNumber: int?", _serializer.SerializeToArray<MaybeNumber>((int?)7));
        AppendPayload(snapshot, "Either<int, string>: Value<string>", _serializer.SerializeToArray<Either<int, string>>(new Value<string>("r")));
        AppendPayload(snapshot, "Shape<int>: Square<int>", _serializer.SerializeToArray<Shape<int>>(new Square<int>(5)));
        return Verify(snapshot.ToString(), extension: "txt").UseDirectory("snapshots");
    }

    private void AppendPayload(StringBuilder snapshot, string label, byte[] payload)
    {
        using var session = _services.GetRequiredService<SerializerSessionPool>().GetSession();
        if (snapshot.Length > 0)
        {
            snapshot.Append("\n\n");
        }

        snapshot.Append("# ").Append(label).Append('\n')
            .Append("hex: ").Append(Convert.ToHexString(payload)).Append('\n')
            .Append(BitStreamFormatter.Format(payload, session).TrimEnd());
    }

    [Fact]
    public void UnionReadsPayloadsWithEncodedRuntimeTypeOrNullReference()
    {
        // Payloads written for a closed type before it used case identifiers encode the runtime type, and encode null as a null reference.
        var value = new Humidity(55) { Location = "attic" };

        Assert.Equal(value, _serializer.Deserialize<SensorUnion>(_serializer.SerializeToArray<object>(value)).Value);
        Assert.Null(_serializer.Deserialize<SensorUnion>(_serializer.SerializeToArray<object?>(null)).Value);
    }

    [Fact]
    public void ClosedHierarchyCaseIdentifiersAboveSixAreSupported()
    {
        // Contact uses case identifier 9, which does not fit in the tag byte.
        var result = RoundTrip<Sensor>(new Contact { Open = true });

        Assert.Equal(new Contact { Open = true }, result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosedHierarchyReadsLegacyPayloadAndWritesTaggedFormat(bool empty)
    {
        // The legacy format uses a runtime-type header for a value and a null reference for null.
        var value = empty ? null : new Humidity(55) { Location = "attic" };
        var legacyBytes = _serializer.SerializeToArray<object?>(value);

        var result = _serializer.Deserialize<Sensor?>(legacyBytes);
        var taggedBytes = _serializer.SerializeToArray<Sensor?>(result);

        Assert.Equal(value, result);
        Assert.Equal(_serializer.SerializeToArray<Sensor?>(value), taggedBytes);
        Assert.NotEqual(legacyBytes, taggedBytes);
        Assert.Equal(value, _serializer.Deserialize<Sensor?>(taggedBytes));
    }

    [Fact]
    public void ClosedHierarchyPreservesNullAndReferences()
    {
        var shared = new Temperature(-4) { Location = "freezer" };
        var holder = new SensorHolder
        {
            First = shared,
            Second = shared,
            Third = null,
            Readings = [shared, new Humidity(10), null],
            Trailing = "after",
        };

        var result = RoundTrip(holder);

        Assert.Equal(shared, result.First);
        Assert.Same(result.First, result.Second);
        Assert.Null(result.Third);
        Assert.Equal(3, result.Readings!.Count);
        Assert.Same(result.First, result.Readings[0]);
        Assert.Equal(new Humidity(10), result.Readings[1]);
        Assert.Null(result.Readings[2]);
        Assert.Equal("after", result.Trailing);
    }

    [Fact]
    public void SkippedUnionFieldsKeepReferencesAligned()
    {
        // A reader without the first field skips it, so it resolves the reference in the second field from the skipped union field.
        var shared = new Temperature(30);
        var bytes = _serializer.SerializeToArray(new SensorHolder { First = shared, Second = shared, Trailing = "after" });

        var result = _serializer.Deserialize<SensorHolderWithoutFirst>(bytes)!;

        Assert.Equal(shared, result.Second);
        Assert.Equal("after", result.Trailing);
    }

    [Fact]
    public void GenericClosedHierarchyRoundTrips()
    {
        Assert.Equal(new Circle<string>("r"), RoundTrip<Shape<string>>(new Circle<string>("r")));
        Assert.Equal(new Circle<int>(4), RoundTrip<Shape<int>>(new Circle<int>(4)));
        Assert.Equal(new Square<long>(5), RoundTrip<Shape<long>>(new Square<long>(5)));
        Assert.Equal(new Square<long>(5), Copy<Shape<long>>(new Square<long>(5)));
        Assert.Equal(new RefBox<string>("x"), RoundTrip<RefHolder<string>>(new RefBox<string>("x")));
    }

    [Fact]
    public void ClosedHierarchyWithoutCaseIdentifiersEncodesRuntimeType()
    {
        var value = new LegacySquare(3);
        var asClosedType = _serializer.SerializeToArray<LegacyShape>(value);
        var asObject = _serializer.SerializeToArray<object>(value);

        Assert.Equal(value, _serializer.Deserialize<LegacyShape>(asClosedType));
        Assert.Equal(asObject.Length, asClosedType.Length);
    }

    public static TheoryData<Pet> Pets() => [(Pet)new Cat("Whiskers"), (Pet)new Dog("Rex"), default(Pet)];

    [Theory]
    [MemberData(nameof(Pets))]
    public void UnionRoundTrips(Pet value)
    {
        var result = RoundTrip(value);

        Assert.Equal(value.Value, result.Value);
        Assert.Equal(value.Value?.GetType(), result.Value?.GetType());
    }

    [Theory]
    [MemberData(nameof(Pets))]
    public void UnionIsDeepCopied(Pet value)
    {
        var result = Copy(value);

        Assert.Equal(value.Value, result.Value);
        if (value.Value is not null)
        {
            Assert.NotSame(value.Value, result.Value);
        }
    }

    [Fact]
    public void UnionCaseIsEncodedAsCaseIdentifier()
    {
        var cat = new Cat("Whiskers");
        var asCase = _serializer.SerializeToArray(cat);
        var asUnion = _serializer.SerializeToArray<Pet>(cat);

        Assert.Equal(asCase.Length + 2, asUnion.Length);
    }

    [Fact]
    public void UnionWithValueTypeCasesRoundTrips()
    {
        Assert.Equal(42, RoundTrip<IntOrString>(42).Value);
        Assert.Equal("text", RoundTrip<IntOrString>("text").Value);
        Assert.Null(RoundTrip(default(IntOrString)).Value);
        Assert.Equal(42, Copy<IntOrString>(42).Value);

        // A value-type case is written with its static codec: union header, case header, value, end.
        Assert.Equal(_serializer.SerializeToArray(42).Length + 2, _serializer.SerializeToArray<IntOrString>(42).Length);
    }

    [Fact]
    public void UnionWithNullableCaseRoundTrips()
    {
        Assert.Equal(7, RoundTrip<MaybeNumber>((int?)7).Value);
        Assert.Equal("seven", RoundTrip<MaybeNumber>("seven").Value);
    }

    [Fact]
    public void UnionCasesWhichShareAGenericDefinitionRoundTrip()
    {
        Either<int, string> right = new Value<string>("r");
        Either<int, string> left = new Value<int>(1);
        Assert.Equal(new Value<string>("r"), RoundTrip(right).Value);
        Assert.Equal(new Value<int>(1), RoundTrip(left).Value);

        // When both cases have the same type, the union cannot distinguish them, and its value round-trips through the first case.
        var same = CreateRight<int, int>(2);
        Assert.Equal(new Value<int>(2), RoundTrip(same).Value);
    }

    private static Either<L, R> CreateRight<L, R>(R value) => new Value<R>(value);

    [Fact]
    public void UnionCaseIdentifiersCanBeAssigned()
    {
        // PetV2 retires the identifier between its cases, so its first case remains readable by PetV1.
        var catBytes = _serializer.SerializeToArray<PetV2>(new Cat("Tom"));
        Assert.Equal(new Cat("Tom"), _serializer.Deserialize<PetV1>(catBytes).Value);
        Assert.Equal(new Dog("Fido"), RoundTrip<PetV2>(new Dog("Fido")).Value);
    }

    [Fact]
    public void UnknownUnionCaseThrows()
    {
        var dogBytes = _serializer.SerializeToArray<PetV2>(new Dog("Fido"));

        Assert.Throws<UnknownUnionCaseException>(() => _serializer.Deserialize<PetV1>(dogBytes));
    }

    [Fact]
    public void UnionFieldsRoundTripInContainers()
    {
        var holder = new SensorHolder { Pet = new Dog("Rex"), Pets = [new Cat("A"), default, new Dog("B")], Trailing = "after" };

        var result = RoundTrip(holder);

        Assert.Equal(new Dog("Rex"), result.Pet.Value);
        Assert.Equal(new object?[] { new Cat("A"), null, new Dog("B") }, result.Pets!.ConvertAll(static pet => pet.Value));
        Assert.Equal("after", result.Trailing);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnionSerializationCallbacksSurroundCaseOperations(bool empty)
    {
        CallbackPet value = empty ? default(CallbackPet) : new Cat("hooks");
        var callbacks = _services.GetRequiredService<PetCallbacks>();
        var bytes = _serializer.SerializeToArray(value);

        Assert.Equal(new (string, object?)[] { ("serializing", value.Value), ("serialized", value.Value) }, callbacks.Events);
        callbacks.Events.Clear();

        var result = _serializer.Deserialize<CallbackPet>(bytes);

        Assert.Equal(value.Value, result.Value);
        Assert.Equal(new[] { ("deserializing", (object?)null), ("deserialized", result.Value) }, callbacks.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnionCopyCallbacksSurroundCaseOperations(bool empty)
    {
        CallbackPet value = empty ? default(CallbackPet) : new Cat("hooks");
        var callbacks = _services.GetRequiredService<PetCallbacks>();

        var result = Copy(value);

        Assert.Equal(value.Value, result.Value);
        Assert.Equal(new[] { ("copying", (object?)null), ("copied", result.Value) }, callbacks.Events);
        if (!empty)
        {
            Assert.NotSame(value.Value, result.Value);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnionDeserializationCallbacksSurroundLegacyCaseValues(bool empty)
    {
        object? value = empty ? null : new Cat("legacy");
        var bytes = _serializer.SerializeToArray(value);
        var callbacks = _services.GetRequiredService<PetCallbacks>();

        var result = _serializer.Deserialize<CallbackPet>(bytes);

        Assert.Equal(value, result.Value);
        Assert.Equal(new[] { ("deserializing", (object?)null), ("deserialized", result.Value) }, callbacks.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnionWithShallowCasesRunsCopyCallbacks(bool empty)
    {
        CallbackNumber value = empty ? default(CallbackNumber) : 42;

        var result = Copy(value);

        Assert.Equal(43, result.Value);
    }

    public static TheoryData<IntOrString> ShallowUnionCases() => [(IntOrString)42, (IntOrString)"text", default(IntOrString)];

    [Theory]
    [MemberData(nameof(ShallowUnionCases))]
    public void UnionCopyClassificationComposesThroughContainers(IntOrString shallow)
    {
        var empty = shallow.Value is null;
        Pet deep = empty ? default(Pet) : new Cat("copy");
        CallbackNumber callback = empty ? default(CallbackNumber) : 42;
        ImmutablePet immutable = empty ? default(ImmutablePet) : new OpaqueCase("shared");
        var holder = new UnionCopyHolder
        {
            Shallow = shallow,
            Deep = deep,
            Callback = callback,
            Immutable = immutable,
            Box = new UnionCopyBox { Shallow = shallow, Deep = deep, Callback = callback, Immutable = immutable },
        };

        var result = Copy(holder);

        Assert.NotSame(holder, result);
        Assert.Equal(shallow.Value, result.Shallow.Value);
        Assert.Equal(shallow.Value, result.Box.Shallow.Value);
        Assert.Equal(deep.Value, result.Deep.Value);
        Assert.Same(result.Deep.Value, result.Box.Deep.Value);
        if (!empty)
        {
            Assert.NotSame(deep.Value, result.Deep.Value);
        }

        Assert.Equal(43, result.Callback.Value);
        Assert.Equal(43, result.Box.Callback.Value);
        Assert.Same(immutable.Value, result.Immutable.Value);
        Assert.Same(immutable.Value, result.Box.Immutable.Value);
    }

    [Fact]
    public void RecursiveUnionCaseGraphCopiesThroughStructWrapper()
    {
        RecursiveUnion value = new RecursiveUnionBox { Value = 42 };

        var result = Copy(value);

        Assert.Equal(42, Assert.IsType<RecursiveUnionBox>(result.Value).Value.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnionDeserializationCallbacksSurroundCaseCallbacks(bool legacy)
    {
        var value = new CallbackCase("ordered");
        var bytes = legacy
            ? _serializer.SerializeToArray<object>(value)
            : _serializer.SerializeToArray<CallbackEnvelope>(value);
        var callbacks = _services.GetRequiredService<CaseOrderCallbacks>();

        var result = _serializer.Deserialize<CallbackEnvelope>(bytes);

        Assert.Equal(value, result.Value);
        Assert.Equal(new[] { "union:before", "case:before", "case:after", "union:after" }, callbacks.Events);
    }

    [Fact]
    public void ImmutableUnionPreservesCaseWithoutRegisteredCopier()
    {
        var value = new OpaqueCase("shared");

        var result = Copy<ImmutablePet>(value);

        Assert.Same(value, result.Value);
    }

    [Fact]
    public void GenericImmutableUnionPreservesCaseReference()
    {
        var value = new Value<List<int>>([1, 2]);

        var result = Copy<ImmutableValue<List<int>>>(value);

        Assert.Same(value, result.Value);
        Assert.Same(value.Item, ((Value<List<int>>)result.Value!).Item);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImmutableUnionRunsCopyCallbacksAndPreservesCaseReference(bool opaque)
    {
        ImmutableCallbackPet value = opaque
            ? new OpaqueCase("shared")
            : new Value<List<int>>([1, 2]);
        var callbacks = _services.GetRequiredService<ImmutablePetCallbacks>();

        var result = Copy(value);

        Assert.Same(value.Value, result.Value);
        Assert.Equal(new[] { "copying", "copied" }, callbacks.Events);
    }
}

[GenerateSerializer]
public closed record Sensor
{
    [Id(0)]
    public string? Location { get; init; }
}

[GenerateSerializer, Case(0)]
public sealed record Temperature([property: Id(0)] double Celsius) : Sensor;

[GenerateSerializer, Case(1)]
public sealed record Humidity([property: Id(0)] double Percent) : Sensor;

[GenerateSerializer, Case(2)]
public closed record Environmental : Sensor;

[GenerateSerializer, Case(0)]
public sealed record Light([property: Id(0)] int Lux) : Environmental;

[GenerateSerializer, Case(9)]
public record Contact : Sensor
{
    [Id(0)]
    public bool Open { get; init; }
}

[GenerateSerializer]
public sealed record ReedContact : Contact
{
    [Id(0)]
    public int Reed { get; init; }
}

/// <summary>
/// A union with the same cases and case identifiers as <see cref="Sensor"/>.
/// </summary>
[GenerateSerializer, CaseIds(0, 1, 2, 9)]
public union SensorUnion(Temperature, Humidity, Environmental, Contact);

// A closed hierarchy whose stateless base is invisible to serialization, and the union which replaces it.
[GenerateSerializer, SerializerTransparent]
public closed record ShapeV1;

[GenerateSerializer, Case(0)]
public sealed record CircleV1([property: Id(0)] double Radius) : ShapeV1;

[GenerateSerializer, Case(1)]
public sealed record SquareV1([property: Id(0)] double Side) : ShapeV1;

[GenerateSerializer]
public sealed record CircleV2([property: Id(0)] double Radius);

[GenerateSerializer]
public sealed record SquareV2([property: Id(0)] double Side);

[GenerateSerializer]
public union ShapeV2(CircleV2, SquareV2);

[GenerateSerializer]
public closed record Shape<T>;

[GenerateSerializer, Case(0)]
public sealed record Circle<T>([property: Id(0)] T Radius) : Shape<T>;

[GenerateSerializer, Case(1)]
public sealed record Square<T>([property: Id(0)] T Side) : Shape<T>;

[GenerateSerializer]
public closed record RefHolder<T> where T : class;

[GenerateSerializer, Case(0)]
public sealed record RefBox<T>([property: Id(0)] T Item) : RefHolder<T> where T : class;

[GenerateSerializer]
public closed record LegacyShape;

[GenerateSerializer]
public sealed record LegacySquare([property: Id(0)] int Side) : LegacyShape;

[GenerateSerializer]
public sealed record Cat([property: Id(0)] string Name);

[GenerateSerializer]
public sealed record Dog([property: Id(0)] string Name);

[GenerateSerializer]
public union Pet(Cat, Dog);

[GenerateSerializer, SerializationCallbacks(typeof(PetCallbacks))]
public union CallbackPet(Cat, Dog);

public sealed class PetCallbacks
{
    public List<(string Event, object? Value)> Events { get; } = [];

    public void OnSerializing(ref CallbackPet value) => Events.Add(("serializing", value.Value));
    public void OnSerialized(CallbackPet value) => Events.Add(("serialized", value.Value));
    public void OnDeserializing(ref CallbackPet value) => Events.Add(("deserializing", value.Value));
    public void OnDeserialized(CallbackPet value) => Events.Add(("deserialized", value.Value));
    public void OnCopying(CallbackPet original, ref CallbackPet result) => Events.Add(("copying", result.Value));
    public void OnCopied(ref CallbackPet original, CallbackPet result) => Events.Add(("copied", result.Value));
}

[Immutable, GenerateSerializer, SerializationCallbacks(typeof(NumberCallbacks))]
public union CallbackNumber(int, string);

public sealed class NumberCallbacks
{
    public void OnCopied(CallbackNumber original, ref CallbackNumber result) => result = 43;
}

[GenerateSerializer]
public sealed class UnionCopyHolder
{
    [Id(0)]
    public IntOrString Shallow { get; set; }

    [Id(1)]
    public Pet Deep { get; set; }

    [Id(2)]
    public CallbackNumber Callback { get; set; }

    [Id(3)]
    public ImmutablePet Immutable { get; set; }

    [Id(4)]
    public UnionCopyBox Box { get; set; }
}

[GenerateSerializer]
public struct UnionCopyBox
{
    [Id(0)]
    public IntOrString Shallow { get; set; }

    [Id(1)]
    public Pet Deep { get; set; }

    [Id(2)]
    public CallbackNumber Callback { get; set; }

    [Id(3)]
    public ImmutablePet Immutable { get; set; }
}

[GenerateSerializer]
public union RecursiveUnion(RecursiveUnionBox, int);

[GenerateSerializer]
public struct RecursiveUnionBox
{
    [Id(0)]
    public RecursiveUnion Value { get; set; }
}

[GenerateSerializer, SerializationCallbacks(typeof(CaseOrderCallbacks))]
public union CallbackEnvelope(CallbackCase);

[GenerateSerializer, SerializationCallbacks(typeof(CaseCallbacks))]
public sealed record CallbackCase([property: Id(0)] string Name);

public sealed class CaseOrderCallbacks
{
    public List<string> Events { get; } = [];

    public void OnDeserializing(ref CallbackEnvelope value) => Events.Add("union:before");
    public void OnDeserialized(CallbackEnvelope value) => Events.Add("union:after");
}

public sealed class CaseCallbacks(CaseOrderCallbacks callbacks)
{
    public void OnDeserializing(CallbackCase value) => callbacks.Events.Add("case:before");
    public void OnDeserialized(CallbackCase value) => callbacks.Events.Add("case:after");
}

public sealed record OpaqueCase(string Name);

[Immutable, GenerateSerializer]
public union ImmutablePet(OpaqueCase);

[Immutable, GenerateSerializer]
public union ImmutableValue<T>(Value<T>);

[Immutable, GenerateSerializer, SerializationCallbacks(typeof(ImmutablePetCallbacks))]
public union ImmutableCallbackPet(OpaqueCase, Value<List<int>>);

public sealed class ImmutablePetCallbacks
{
    public List<string> Events { get; } = [];

    public void OnCopying(ImmutableCallbackPet original, ref ImmutableCallbackPet result) => Events.Add("copying");
    public void OnCopied(ImmutableCallbackPet original, ref ImmutableCallbackPet result) => Events.Add("copied");
}

[GenerateSerializer]
public union PetV1(Cat);

[GenerateSerializer, CaseIds(0, 2)]
public union PetV2(Cat, Dog);

[GenerateSerializer]
public union IntOrString(int, string);

[GenerateSerializer]
public union MaybeNumber(int?, string);

[GenerateSerializer]
public sealed record Value<T>([property: Id(0)] T Item);

// The cases intentionally share a generic definition to verify first-case semantics when the type arguments are equal.
[GenerateSerializer]
public union Either<L, R>(Value<L>, Value<R>);

[GenerateSerializer]
public sealed class SensorHolder
{
    [Id(0)]
    public Sensor? First { get; set; }

    [Id(1)]
    public Sensor? Second { get; set; }

    [Id(2)]
    public Sensor? Third { get; set; }

    [Id(3)]
    public List<Sensor?>? Readings { get; set; }

    [Id(4)]
    public Pet Pet { get; set; }

    [Id(5)]
    public List<Pet>? Pets { get; set; }

    [Id(6)]
    public string? Trailing { get; set; }
}

[GenerateSerializer]
public sealed class SensorHolderWithoutFirst
{
    [Id(1)]
    public Sensor? Second { get; set; }

    [Id(6)]
    public string? Trailing { get; set; }
}
