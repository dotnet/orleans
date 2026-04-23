using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Session;
using Orleans.Serialization.Utilities;
using UnitTests.SerializerExternalModels;
using Xunit;
#if !NETCOREAPP3_1
using static VerifyXunit.Verifier;
#endif

namespace Orleans.Serialization.UnitTests;

/// <summary>
/// Locks in representative generated-serializer payloads so future source-generator changes
/// can be checked for wire-format drift without deserializing newly produced data.
/// </summary>
[Trait("Category", "BVT")]
public sealed class GeneratedSerializerBitwiseCompatibilityTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;

    public GeneratedSerializerBitwiseCompatibilityTests()
    {
        _serviceProvider = new ServiceCollection()
            .AddSerializer()
            .BuildServiceProvider();
    }

    [Fact]
    public void GeneratedClassSerializer_ExactType_MatchesBaseline()
        => AssertBitwiseCompatibility(SerializeAs(CreateBaselineClass()));

#if !NETCOREAPP3_1
    [Fact]
    public Task GeneratedClassSerializer_ExactType_Formatted_MatchesSnapshot()
        => Verify(FormatSerializedPayload(SerializeAs(CreateBaselineClass())), extension: "txt").UseDirectory("snapshots");
#endif

    [Fact]
    public void GeneratedRecordSerializer_UntypedRoot_MatchesBaseline()
        => AssertBitwiseCompatibility(SerializeAs<object>(CreateBaselinePerson()));

#if !NETCOREAPP3_1
    [Fact]
    public Task GeneratedRecordSerializer_UntypedRoot_Formatted_MatchesSnapshot()
        => Verify(FormatSerializedPayload(SerializeAs<object>(CreateBaselinePerson())), extension: "txt").UseDirectory("snapshots");
#endif

    [Fact]
    public void GeneratedAutoFieldIdSerializer_ExactType_MatchesBaseline()
        => AssertBitwiseCompatibility(SerializeAs(CreateBaselineAutoProperties()));

#if !NETCOREAPP3_1
    [Fact]
    public Task GeneratedAutoFieldIdSerializer_ExactType_Formatted_MatchesSnapshot()
        => Verify(FormatSerializedPayload(SerializeAs(CreateBaselineAutoProperties())), extension: "txt").UseDirectory("snapshots");
#endif

    [Fact]
    public void GeneratedPolymorphicSerializer_BaseType_MatchesBaseline()
        => AssertBitwiseCompatibility(SerializeAs<BaselineBase>(CreateBaselineDerived()));

#if !NETCOREAPP3_1
    [Fact]
    public Task GeneratedPolymorphicSerializer_BaseType_Formatted_MatchesSnapshot()
        => Verify(FormatSerializedPayload(SerializeAs<BaselineBase>(CreateBaselineDerived())), extension: "txt").UseDirectory("snapshots");
#endif

    [Fact]
    public void Person3_ExactType_MatchesBaseline()
        => AssertBitwiseCompatibility(SerializeAs(CreatePerson3()));

#if !NETCOREAPP3_1
    [Fact]
    public Task Person3_ExactType_Formatted_MatchesSnapshot()
        => Verify(FormatSerializedPayload(SerializeAs(CreatePerson3())), extension: "txt").UseDirectory("snapshots");
#endif

    [Fact]
    public void Person4_ExactType_MatchesBaseline()
        => AssertBitwiseCompatibility(SerializeAs(CreatePerson4()));

    [Fact]
    public void Person5_ExactType_MatchesBaseline()
        => AssertBitwiseCompatibility(SerializeAs(CreatePerson5()));

#if !NETCOREAPP3_1
    [Fact]
    public Task Person5_ExactType_Formatted_MatchesSnapshot()
        => Verify(FormatSerializedPayload(SerializeAs(CreatePerson5())), extension: "txt").UseDirectory("snapshots");
#endif

    [Fact]
    public void Person5_RecordAndClass_AreBitwiseEquivalent()
    {
        var recordBytes = SerializeAs(CreatePerson5());
        var classBytes = SerializeAs(CreatePerson5Class());

        Assert.Equal(recordBytes, classBytes);
    }

#if NET6_0_OR_GREATER
    [Fact]
    public void Person2ExternalStruct_ExactType_MatchesBaseline()
        => AssertBitwiseCompatibility(SerializeAs(CreatePerson2ExternalStruct()));

    [Fact]
    public void Person2External_ExactType_MatchesBaseline()
        => AssertBitwiseCompatibility(SerializeAs(CreatePerson2External()));

#if !NETCOREAPP3_1
    [Fact]
    public Task Person2External_ExactType_Formatted_MatchesSnapshot()
        => Verify(FormatSerializedPayload(SerializeAs(CreatePerson2External())), extension: "txt").UseDirectory("snapshots");
#endif

    [Fact]
    public void GenericPersonExternalStruct_ExactType_MatchesBaseline()
        => AssertBitwiseCompatibility(SerializeAs(CreateGenericPersonExternalStruct()));

#if !NETCOREAPP3_1
    [Fact]
    public Task GenericPersonExternalStruct_ExactType_Formatted_MatchesSnapshot()
        => Verify(FormatSerializedPayload(SerializeAs(CreateGenericPersonExternalStruct())), extension: "txt").UseDirectory("snapshots");
#endif

    [Fact]
    public void ReadonlyGenericPersonExternalStruct_ExactType_MatchesBaseline()
        => AssertBitwiseCompatibility(SerializeAs(CreateReadonlyGenericPersonExternalStruct()));

#if NET9_0_OR_GREATER
    [Fact]
    public void GenericPersonExternal_ExactType_MatchesBaseline()
        => AssertBitwiseCompatibility(SerializeAs(CreateGenericPersonExternal()));

#if !NETCOREAPP3_1
    [Fact]
    public Task GenericPersonExternal_ExactType_Formatted_MatchesSnapshot()
        => Verify(FormatSerializedPayload(SerializeAs(CreateGenericPersonExternal())), extension: "txt").UseDirectory("snapshots");
#endif
#endif
#endif

    [Fact]
    public void GenericPocoString_Untyped_MatchesBaseline()
        => AssertBitwiseCompatibility(SerializeAs<object>(CreateGenericPocoString()));

#if !NETCOREAPP3_1
    [Fact]
    public Task GenericPocoString_Untyped_Formatted_MatchesSnapshot()
        => Verify(FormatSerializedPayload(SerializeAs<object>(CreateGenericPocoString())), extension: "txt").UseDirectory("snapshots");
#endif

    [Fact]
    public void NestedGenericPoco_Untyped_MatchesBaseline()
        => AssertBitwiseCompatibility(SerializeAs<object>(CreateNestedGenericPoco()));

#if !NETCOREAPP3_1
    [Fact]
    public Task NestedGenericPoco_Untyped_Formatted_MatchesSnapshot()
        => Verify(FormatSerializedPayload(SerializeAs<object>(CreateNestedGenericPoco())), extension: "txt").UseDirectory("snapshots");
#endif

    [Fact]
    public void OuterInnerGen_Untyped_MatchesBaseline()
        => AssertBitwiseCompatibility(SerializeAs<object>(CreateOuterInnerGen()));

    [Fact]
    public void OuterInnerNonGen_Untyped_MatchesBaseline()
        => AssertBitwiseCompatibility(SerializeAs<object>(CreateOuterInnerNonGen()));

    [Fact]
    public void SerializableClassWithCompiledBase_ExactType_MatchesBaseline()
        => AssertBitwiseCompatibility(SerializeAs(CreateSerializableClassWithCompiledBase()));

    [Fact]
    public void DerivedFromDictionary_ExactType_MatchesBaseline()
        => AssertBitwiseCompatibility(SerializeAs(CreateDerivedFromDictionary()));

#if !NETCOREAPP3_1
    [Fact]
    public Task DerivedFromDictionary_ExactType_Formatted_MatchesSnapshot()
        => Verify(FormatSerializedPayload(SerializeAs(CreateDerivedFromDictionary())), extension: "txt").UseDirectory("snapshots");
#endif

#if NET6_0_OR_GREATER
    [Fact]
    public void ClassWithRequiredMembers_ExactType_MatchesBaseline()
        => AssertBitwiseCompatibility(SerializeAs(CreateClassWithRequiredMembers()));

    [Fact]
    public void SubClassWithRequiredMembersInBase_ExactType_MatchesBaseline()
        => AssertBitwiseCompatibility(SerializeAs(CreateSubClassWithRequiredMembersInBase()));

#if !NETCOREAPP3_1
    [Fact]
    public Task ClassWithRequiredMembers_ExactType_Formatted_MatchesSnapshot()
        => Verify(FormatSerializedPayload(SerializeAs(CreateClassWithRequiredMembers())), extension: "txt").UseDirectory("snapshots");
#endif
#endif

    [Fact]
    public void ImmutableClass_ExactType_MatchesBaseline()
        => AssertBitwiseCompatibility(SerializeAs(CreateImmutableClass()));

    [Fact]
    public void ImmutableStruct_ExactType_MatchesBaseline()
        => AssertBitwiseCompatibility(SerializeAs(CreateImmutableStruct()));

    [Fact]
    public void ClassWithImplicitFieldIds_ExactType_MatchesBaseline()
        => AssertBitwiseCompatibility(SerializeAs(CreateClassWithImplicitFieldIds()));

    [Fact]
    public void DuplicateReferences_ExactType_MatchesBaseline()
        => AssertBitwiseCompatibility(SerializeAs<object[]>(CreateDuplicateReferences()));

#if !NETCOREAPP3_1
    [Fact]
    public Task DuplicateReferences_ExactType_Formatted_MatchesSnapshot()
        => Verify(FormatSerializedPayload(SerializeAs<object[]>(CreateDuplicateReferences())), extension: "txt").UseDirectory("snapshots");
#endif

    [Fact]
    public void DuplicateReferencesSuppressTracking_ExactType_MatchesBaseline()
        => AssertBitwiseCompatibility(SerializeAs<object[]>(CreateDuplicateSuppressTrackingReferences()));

#if !NETCOREAPP3_1
    [Fact]
    public Task DuplicateReferencesSuppressTracking_ExactType_Formatted_MatchesSnapshot()
        => Verify(FormatSerializedPayload(SerializeAs<object[]>(CreateDuplicateSuppressTrackingReferences())), extension: "txt").UseDirectory("snapshots");
#endif

    [Fact]
    public void ClassWithTypeFields_Untyped_MatchesBaseline()
        => AssertBitwiseCompatibility(SerializeAs<object>(CreateClassWithTypeFields()));

#if !NETCOREAPP3_1
    [Fact]
    public Task ClassWithTypeFields_Untyped_Formatted_MatchesSnapshot()
        => Verify(FormatSerializedPayload(SerializeAs<object>(CreateClassWithTypeFields())), extension: "txt").UseDirectory("snapshots");
#endif

    [Fact]
    public void WrapsMyForeignLibraryType_ExactType_MatchesBaseline()
        => AssertBitwiseCompatibility(SerializeAs(CreateWrapsMyForeignLibraryType()));

#if !NETCOREAPP3_1
    [Fact]
    public Task WrapsMyForeignLibraryType_ExactType_Formatted_MatchesSnapshot()
        => Verify(FormatSerializedPayload(SerializeAs(CreateWrapsMyForeignLibraryType())), extension: "txt").UseDirectory("snapshots");
#endif

    [Fact]
    public void MyFirstForeignLibraryType_ExactType_MatchesBaseline()
        => AssertBitwiseCompatibility(SerializeAs(CreateMyFirstForeignLibraryType()));

#if !NETCOREAPP3_1
    [Fact]
    public Task MyFirstForeignLibraryType_ExactType_Formatted_MatchesSnapshot()
        => Verify(FormatSerializedPayload(SerializeAs(CreateMyFirstForeignLibraryType())), extension: "txt").UseDirectory("snapshots");
#endif

    [Fact]
    public void MySecondForeignLibraryType_ExactType_MatchesBaseline()
        => AssertBitwiseCompatibility(SerializeAs(CreateMySecondForeignLibraryType()));

#if !NETCOREAPP3_1
    [Fact]
    public Task MySecondForeignLibraryType_ExactType_Formatted_MatchesSnapshot()
        => Verify(FormatSerializedPayload(SerializeAs(CreateMySecondForeignLibraryType())), extension: "txt").UseDirectory("snapshots");
#endif

    public void Dispose() => _serviceProvider.Dispose();

    private byte[] SerializeAs<T>(T value)
    {
        var serializer = _serviceProvider.GetRequiredService<Serializer<T>>();
        return serializer.SerializeToArray(value);
    }

    private void AssertBitwiseCompatibility(
        byte[] actualBytes,
        [CallerFilePath] string sourceFile = "",
        [CallerMemberName] string testName = "")
    {
        var expectedPath = Path.Combine(Path.GetDirectoryName(sourceFile)!, "snapshots", $"{nameof(GeneratedSerializerBitwiseCompatibilityTests)}.{testName}.verified.hex.txt");
        // Keep the checked-in baseline as inert text: normalize and compare the hex string,
        // but never decode it back into bytes or deserialize it into an object graph.
        var normalizedExpected = NormalizeHex(File.ReadAllText(expectedPath));
        var actualHex = ToHexString(actualBytes);

        Assert.True(
            string.Equals(normalizedExpected, actualHex, StringComparison.Ordinal),
            $"Serialized bytes changed.{Environment.NewLine}Baseline: {expectedPath}{Environment.NewLine}Expected: {normalizedExpected}{Environment.NewLine}Actual:   {actualHex}");
    }

    private static string NormalizeHex(string value) => string.Concat(value.Where(static c => !char.IsWhiteSpace(c)));

    private static string ToHexString(byte[] value) => string.Concat(value.Select(static b => b.ToString("X2")));

    private string FormatSerializedPayload(byte[] payload)
    {
        using var session = _serviceProvider.GetRequiredService<SerializerSessionPool>().GetSession();
        return BitStreamFormatter.Format(payload, session);
    }

    private static DateTimeOffset CreateTimestamp() => new(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static BaselineClass CreateBaselineClass() =>
        new()
        {
            IntField = 2,
            IntProperty = 30,
            OtherObject = BaselineEnum.Two,
        };

    private static BaselinePerson CreateBaselinePerson() =>
        new(2, "harry")
        {
            FavouriteColor = "redborine",
            StarSign = "Aquaricorn",
        };

    private static BaselineAutoProperties CreateBaselineAutoProperties() =>
        new()
        {
            A = 1,
            B = 2,
            C = 3,
            D = 4,
            E = 5,
            F = 6,
            G = 7,
            H = 8,
            I = 9,
            J = 10,
            K = 11,
        };

    private static BaselineDerived CreateBaselineDerived() =>
        new()
        {
            BaseValue = new BaselineValue { Value = 11 },
            SubValue = new BaselineValue { Value = 22 },
        };

    private static Person3 CreatePerson3() =>
        new(2, "harry")
        {
            FavouriteColor = "redborine",
            StarSign = "Aquaricorn",
        };

    private static Person4 CreatePerson4() => new(2, "harry");

    private static Person5 CreatePerson5() =>
        new(2, "harry")
        {
            FavouriteColor = "redborine",
            StarSign = "Aquaricorn",
        };

    private static Person5_Class CreatePerson5Class() =>
        new()
        {
            Age = 2,
            Name = "harry",
            FavouriteColor = "redborine",
            StarSign = "Aquaricorn",
        };

#if NET6_0_OR_GREATER
    private static Person2ExternalStruct CreatePerson2ExternalStruct() =>
        new(2, "harry")
        {
            FavouriteColor = "redborine",
            StarSign = "Aquaricorn",
        };

    private static Person2External CreatePerson2External() =>
        new(2, "harry")
        {
            FavouriteColor = "redborine",
            StarSign = "Aquaricorn",
        };

    private static GenericPersonExternalStruct<Person2External> CreateGenericPersonExternalStruct()
    {
        var inner = CreatePerson2External();
        return new GenericPersonExternalStruct<Person2External>(inner, "harry")
        {
            BodyParam = inner,
            StarSign = "Aquaricorn",
        };
    }

    private static ReadonlyGenericPersonExternalStruct<Person2External> CreateReadonlyGenericPersonExternalStruct()
    {
        var inner = CreatePerson2External();
        return new ReadonlyGenericPersonExternalStruct<Person2External>(inner, "harry")
        {
            BodyParam = inner,
            StarSign = "Aquaricorn",
        };
    }

#if NET9_0_OR_GREATER
    private static GenericPersonExternal<Person2External> CreateGenericPersonExternal()
    {
        var inner = CreatePerson2External();
        return new GenericPersonExternal<Person2External>(inner, "harry")
        {
            BodyParam = inner,
            StarSign = "Aquaricorn",
        };
    }
#endif
#endif

    private static GenericPoco<string> CreateGenericPocoString() =>
        new()
        {
            Field = "0123456789ABCDEF",
            ArrayField = ["a", "bb", "ccc"],
        };

    private static GenericPoco<GenericPoco<string>> CreateNestedGenericPoco()
    {
        var inner = new GenericPoco<string>
        {
            Field = "nested-value",
            ArrayField = ["inner-a", "inner-b"],
        };

        return new GenericPoco<GenericPoco<string>>
        {
            Field = inner,
            ArrayField = [inner],
        };
    }

    private static Outer<int>.InnerGen<string> CreateOuterInnerGen() => new();

    private static Outer<int>.InnerNonGen CreateOuterInnerNonGen() => new();

    private static SerializableClassWithCompiledBase CreateSerializableClassWithCompiledBase()
    {
        var result = new SerializableClassWithCompiledBase { IntProperty = 30 };
        result.Add(1);
        result.Add(200);
        return result;
    }

    private static DerivedFromDictionary<string, int> CreateDerivedFromDictionary()
    {
        var result = new DerivedFromDictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            IntProperty = 123,
            ["a"] = 1,
            ["b"] = 2,
        };
        return result;
    }

#if NET6_0_OR_GREATER
    private static ClassWithRequiredMembers CreateClassWithRequiredMembers() =>
        new()
        {
            IntProperty = 1,
            StringField = "foo",
        };

    private static SubClassWithRequiredMembersInBase CreateSubClassWithRequiredMembersInBase() =>
        new()
        {
            IntProperty = 2,
            StringField = "bar",
        };
#endif

    private static ImmutableClass CreateImmutableClass() => new(30, 2, 88, 99);

    private static ImmutableStruct CreateImmutableStruct() => new(30, 2);

    private static ClassWithImplicitFieldIds CreateClassWithImplicitFieldIds() => new("apples", MyCustomEnum.One);

    private static object[] CreateDuplicateReferences()
    {
        var sharedObject = new MyValue(1);
        return [sharedObject, sharedObject];
    }

    private static object[] CreateDuplicateSuppressTrackingReferences()
    {
        var sharedObject = new MySuppressReferenceTrackingValue(1);
        return [sharedObject, sharedObject];
    }

    private static ClassWithTypeFields CreateClassWithTypeFields() =>
        new()
        {
            Type1 = typeof(MyValue),
            UntypedValue = new MyValue(42),
            Type2 = typeof(MyValue),
        };

    private static MyForeignLibraryType CreateMyForeignLibraryType() => new(2, "three", CreateTimestamp());

    private static WrapsMyForeignLibraryType CreateWrapsMyForeignLibraryType() =>
        new()
        {
            IntValue = 1,
            ForeignValue = CreateMyForeignLibraryType(),
            OtherIntValue = 4,
        };

    private static MyFirstForeignLibraryType CreateMyFirstForeignLibraryType() =>
        new()
        {
            Num = 2,
            String = "three",
            DateTimeOffset = CreateTimestamp(),
        };

    private static MySecondForeignLibraryType CreateMySecondForeignLibraryType() =>
        new()
        {
            Name = "surrogate",
            Value = 42.5f,
            Timestamp = CreateTimestamp(),
        };
}

[GenerateSerializer]
public sealed class BaselineClass
{
    [Id(0)]
    public int IntProperty { get; set; }

    [Id(1)]
    public int IntField;

    [Id(2)]
    public object OtherObject { get; set; }
}

[GenerateSerializer]
public enum BaselineEnum
{
    None,
    One,
    Two,
}

[Alias("baseline.person"), GenerateSerializer]
public sealed record BaselinePerson([property: Id(0)] int Age, [property: Id(1)] string Name)
{
    [Id(2)]
    public string FavouriteColor { get; init; }

    [Id(3)]
    public string StarSign { get; init; }
}

[GenerateSerializer(GenerateFieldIds = GenerateFieldIds.PublicProperties)]
public sealed class BaselineAutoProperties
{
    public int A { get; set; }
    public int B { get; set; }
    public int C { get; set; }
    public int D { get; set; }
    public int E { get; set; }
    public int F { get; set; }
    public int G { get; set; }
    public int H { get; set; }
    public int I { get; set; }
    public int J { get; set; }
    public int K { get; set; }
}

[GenerateSerializer]
public class BaselineBase
{
    [Id(0)]
    public BaselineValue BaseValue { get; set; }
}

[GenerateSerializer]
public sealed class BaselineDerived : BaselineBase
{
    [Id(0)]
    public BaselineValue SubValue { get; set; }
}

[GenerateSerializer]
public sealed class BaselineValue
{
    [Id(0)]
    public int Value { get; set; }
}
