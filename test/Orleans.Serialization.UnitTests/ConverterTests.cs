using System;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.TestKit;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Serialization.UnitTests;

/// <summary>
/// Tests for the converter-based serialization mechanism in Orleans.
/// 
/// Converters allow Orleans to serialize types from external libraries that cannot be modified
/// to add Orleans serialization attributes. This is achieved through surrogate types that:
/// - Act as intermediaries for serialization
/// - Convert between the foreign type and a serializable representation
/// - Support both reference types and value types
/// - Allow serialization of derived types from foreign libraries
/// 
/// This approach enables Orleans to maintain its high-performance serialization while
/// integrating with third-party libraries and legacy code.
/// </summary>
public class ConverterCodecTests : FieldCodecTester<MyForeignLibraryType, IFieldCodec<MyForeignLibraryType>>, IClassFixture<SerializationTesterFixture>
{
    public ConverterCodecTests(ITestOutputHelper output, SerializationTesterFixture fixture) : base(output, fixture)
    {
    }

    protected override MyForeignLibraryType CreateValue() => new(12, "hi", DateTimeOffset.Now);
    protected override bool Equals(MyForeignLibraryType left, MyForeignLibraryType right) => ReferenceEquals(left, right) || left.Equals(right);
    protected override MyForeignLibraryType[] TestValues => new MyForeignLibraryType[] { null, CreateValue() };
}

public class ConverterCopierTests : CopierTester<MyForeignLibraryType, IDeepCopier<MyForeignLibraryType>>, IClassFixture<SerializationTesterFixture>
{
    public ConverterCopierTests(ITestOutputHelper output, SerializationTesterFixture fixture) : base(output, fixture)
    {
    }

    protected override MyForeignLibraryType CreateValue() => new(12, "hi", DateTimeOffset.Now);
    protected override bool Equals(MyForeignLibraryType left, MyForeignLibraryType right) => ReferenceEquals(left, right) || left.Equals(right);
    protected override MyForeignLibraryType[] TestValues => new MyForeignLibraryType[] { null, CreateValue() };
}

public class WrappedConverterCodecTests : FieldCodecTester<WrapsMyForeignLibraryType, IFieldCodec<WrapsMyForeignLibraryType>>, IClassFixture<SerializationTesterFixture>
{
    public WrappedConverterCodecTests(ITestOutputHelper output, SerializationTesterFixture fixture) : base(output, fixture)
    {
    }

    protected override WrapsMyForeignLibraryType CreateValue() => new() { IntValue = 12, ForeignValue = new MyForeignLibraryType(12, "hi", DateTimeOffset.Now), OtherIntValue = 7468249 };
    protected override bool Equals(WrapsMyForeignLibraryType left, WrapsMyForeignLibraryType right) => ReferenceEquals(left, right) || left.Equals(right);
    protected override WrapsMyForeignLibraryType[] TestValues => new WrapsMyForeignLibraryType[] { default, CreateValue() };
}

public class WrappedConverterCopierTests : CopierTester<WrapsMyForeignLibraryType, IDeepCopier<WrapsMyForeignLibraryType>>, IClassFixture<SerializationTesterFixture>
{
    public WrappedConverterCopierTests(ITestOutputHelper output, SerializationTesterFixture fixture) : base(output, fixture)
    {
    }

    protected override WrapsMyForeignLibraryType CreateValue() => new() { IntValue = 12, ForeignValue = new MyForeignLibraryType(12, "hi", DateTimeOffset.Now), OtherIntValue = 7468249 };
    protected override bool Equals(WrapsMyForeignLibraryType left, WrapsMyForeignLibraryType right) => ReferenceEquals(left, right) || left.Equals(right);
    protected override WrapsMyForeignLibraryType[] TestValues => new WrapsMyForeignLibraryType[] { default, CreateValue() };
}

public class StructConverterCodecTests : ValueTypeFieldCodecTester<MyForeignLibraryValueType, IFieldCodec<MyForeignLibraryValueType>>, IClassFixture<SerializationTesterFixture>
{
    public StructConverterCodecTests(ITestOutputHelper output, SerializationTesterFixture fixture) : base(output, fixture)
    {
    }

    protected override MyForeignLibraryValueType CreateValue() => new(12, "hi", DateTimeOffset.Now);
    protected override bool Equals(MyForeignLibraryValueType left, MyForeignLibraryValueType right) => left.Equals(right);
    protected override MyForeignLibraryValueType[] TestValues => new MyForeignLibraryValueType[] { default, CreateValue() };
}

public class StructConverterCopierTests : CopierTester<MyForeignLibraryValueType, IDeepCopier<MyForeignLibraryValueType>>, IClassFixture<SerializationTesterFixture>
{
    public StructConverterCopierTests(ITestOutputHelper output, SerializationTesterFixture fixture) : base(output, fixture)
    {
    }

    protected override MyForeignLibraryValueType CreateValue() => new(12, "hi", DateTimeOffset.Now);
    protected override bool Equals(MyForeignLibraryValueType left, MyForeignLibraryValueType right) => left.Equals(right);
    protected override MyForeignLibraryValueType[] TestValues => new MyForeignLibraryValueType[] { default, CreateValue() };
}

public class WrappedStructConverterCodecTests : ValueTypeFieldCodecTester<WrapsMyForeignLibraryValueType, IFieldCodec<WrapsMyForeignLibraryValueType>>, IClassFixture<SerializationTesterFixture>
{
    public WrappedStructConverterCodecTests(ITestOutputHelper output, SerializationTesterFixture fixture) : base(output, fixture)
    {
    }

    protected override WrapsMyForeignLibraryValueType CreateValue() => new() { IntValue = 12, ForeignValue = new MyForeignLibraryValueType(12, "hi", DateTimeOffset.Now), OtherIntValue = 7468249 };
    protected override bool Equals(WrapsMyForeignLibraryValueType left, WrapsMyForeignLibraryValueType right) => left.Equals(right);
    protected override WrapsMyForeignLibraryValueType[] TestValues => new WrapsMyForeignLibraryValueType[] { default, CreateValue() };
}

public class WrappedStructConverterCopierTests : CopierTester<WrapsMyForeignLibraryValueType, IDeepCopier<WrapsMyForeignLibraryValueType>>, IClassFixture<SerializationTesterFixture>
{
    public WrappedStructConverterCopierTests(ITestOutputHelper output, SerializationTesterFixture fixture) : base(output, fixture)
    {
    }

    protected override WrapsMyForeignLibraryValueType CreateValue() => new() { IntValue = 12, ForeignValue = new MyForeignLibraryValueType(12, "hi", DateTimeOffset.Now), OtherIntValue = 7468249 };
    protected override bool Equals(WrapsMyForeignLibraryValueType left, WrapsMyForeignLibraryValueType right) => left.Equals(right);
    protected override WrapsMyForeignLibraryValueType[] TestValues => new WrapsMyForeignLibraryValueType[] { default, CreateValue() };
}

public class DerivedConverterCodecTests : FieldCodecTester<DerivedFromMyForeignLibraryType, IFieldCodec<DerivedFromMyForeignLibraryType>>, IClassFixture<SerializationTesterFixture>
{
    public DerivedConverterCodecTests(ITestOutputHelper output, SerializationTesterFixture fixture) : base(output, fixture)
    {
    }

    protected override DerivedFromMyForeignLibraryType CreateValue() => new(658, 12, "hi", DateTimeOffset.Now);
    protected override bool Equals(DerivedFromMyForeignLibraryType left, DerivedFromMyForeignLibraryType right) => ReferenceEquals(left, right) || left.Equals(right);
    protected override DerivedFromMyForeignLibraryType[] TestValues => new DerivedFromMyForeignLibraryType[] { null, CreateValue() };
}

public class DerivedConverterCopierTests : CopierTester<DerivedFromMyForeignLibraryType, IDeepCopier<DerivedFromMyForeignLibraryType>>, IClassFixture<SerializationTesterFixture>
{
    public DerivedConverterCopierTests(ITestOutputHelper output, SerializationTesterFixture fixture) : base(output, fixture)
    {
    }

    protected override DerivedFromMyForeignLibraryType CreateValue() => new(658, 12, "hi", DateTimeOffset.Now);
    protected override bool Equals(DerivedFromMyForeignLibraryType left, DerivedFromMyForeignLibraryType right) => ReferenceEquals(left, right) || left.Equals(right);
    protected override DerivedFromMyForeignLibraryType[] TestValues => new DerivedFromMyForeignLibraryType[] { null, CreateValue() };
}


public class CombinedConverterCopierTests : CopierTester<MyFirstForeignLibraryType, IDeepCopier<MyFirstForeignLibraryType>>, IClassFixture<SerializationTesterFixture>
{
    public CombinedConverterCopierTests(ITestOutputHelper output, SerializationTesterFixture fixture) : base(output, fixture)
    {
    }

    protected override MyFirstForeignLibraryType CreateValue() => new() { Num = 12, String = "hi", DateTimeOffset = DateTimeOffset.Now };
    protected override bool Equals(MyFirstForeignLibraryType left, MyFirstForeignLibraryType right) => left.Equals(right);
    protected override MyFirstForeignLibraryType[] TestValues => new MyFirstForeignLibraryType[] { CreateValue() };
}
