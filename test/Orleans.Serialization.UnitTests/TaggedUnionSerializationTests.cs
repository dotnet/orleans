#nullable enable
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Orleans.Serialization.UnitTests.TaggedUnions;

/// <summary>
/// Tests tagged-union serialization of closed type hierarchies and union types.
/// </summary>
[Trait("Category", "BVT")]
[Trait("Suite", "BVT")]
[Trait("Provider", "None")]
[Trait("Area", "Serialization")]
public class TaggedUnionSerializationTests
{
    private readonly ServiceProvider _services;
    private readonly Serializer _serializer;
    private readonly DeepCopier _copier;

    public TaggedUnionSerializationTests()
    {
        _services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        _serializer = _services.GetRequiredService<Serializer>();
        _copier = _services.GetRequiredService<DeepCopier>();
    }

    private T RoundTrip<T>(T value) => _serializer.Deserialize<T>(_serializer.SerializeToArray(value))!;

    private T Copy<T>(T value) => _copier.GetCopier<T>().Copy(value)!;

    public static TheoryData<Sensor> Sensors() =>
    [
        new Temperature(21.5) { Location = "kitchen" },
        new Humidity(40),
        new Contact { Open = true },
        new ReedContact { Open = false, Reed = 3 },
        new Light(800) { Location = "porch" },
    ];

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

    [Theory]
    [MemberData(nameof(Sensors))]
    public void ClosedHierarchyAndEquivalentUnionHaveTheSameEncoding(Sensor value)
    {
        SensorUnion union = value switch
        {
            Temperature temperature => temperature,
            Humidity humidity => humidity,
            Environmental environmental => environmental,
            Contact contact => contact,
        };

        var closedBytes = _serializer.SerializeToArray<Sensor>(value);
        var unionBytes = _serializer.SerializeToArray(union);

        Assert.Equal(closedBytes, unionBytes);
        Assert.Equal(value, _serializer.Deserialize<SensorUnion>(closedBytes).Value);
        Assert.Equal(value, _serializer.Deserialize<Sensor>(unionBytes));
    }

    [Fact]
    public void NullClosedValueAndEmptyUnionHaveTheSameEncoding()
    {
        var closedBytes = _serializer.SerializeToArray<Sensor?>(null);
        var unionBytes = _serializer.SerializeToArray(default(SensorUnion));

        Assert.Equal(closedBytes, unionBytes);
        Assert.Null(_serializer.Deserialize<Sensor?>(unionBytes));
        Assert.Null(_serializer.Deserialize<SensorUnion>(closedBytes).Value);
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

    [Fact]
    public void ClosedHierarchyReadsPayloadsWithEncodedRuntimeType()
    {
        // Payloads written before a closed type declared case identifiers encode the runtime type in the field header.
        var value = new Humidity(55) { Location = "attic" };
        var bytes = _serializer.SerializeToArray<object>(value);

        var result = _serializer.Deserialize<Sensor>(bytes);

        Assert.Equal(value, result);
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
#pragma warning disable ORLEANS0028
[GenerateSerializer]
public union Either<L, R>(Value<L>, Value<R>);
#pragma warning restore ORLEANS0028

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
