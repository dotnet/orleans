using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.TestKit;
using Xunit;

namespace Orleans.Serialization.UnitTests;

public class ConverterCodecTests : FieldCodecTester<MyForeignLibraryType, IFieldCodec<MyForeignLibraryType>>
{
    protected override MyForeignLibraryType CreateValue() => new(12, "hi", DateTimeOffset.Now);
    protected override bool Equals(MyForeignLibraryType left, MyForeignLibraryType right) => ReferenceEquals(left, right) || left.Equals(right);
    protected override MyForeignLibraryType[] TestValues => new MyForeignLibraryType[] { null, CreateValue() };
}

public class ConverterCopierTests : CopierTester<MyForeignLibraryType, IDeepCopier<MyForeignLibraryType>>
{
    protected override MyForeignLibraryType CreateValue() => new(12, "hi", DateTimeOffset.Now);
    protected override bool Equals(MyForeignLibraryType left, MyForeignLibraryType right) => ReferenceEquals(left, right) || left.Equals(right);
    protected override MyForeignLibraryType[] TestValues => new MyForeignLibraryType[] { null, CreateValue() };
}

public class WrappedConverterCodecTests : FieldCodecTester<WrapsMyForeignLibraryType, IFieldCodec<WrapsMyForeignLibraryType>>
{
    protected override WrapsMyForeignLibraryType CreateValue() => new() { IntValue = 12, ForeignValue = new MyForeignLibraryType(12, "hi", DateTimeOffset.Now), OtherIntValue = 7468249 };
    protected override bool Equals(WrapsMyForeignLibraryType left, WrapsMyForeignLibraryType right) => ReferenceEquals(left, right) || left.Equals(right);
    protected override WrapsMyForeignLibraryType[] TestValues => new WrapsMyForeignLibraryType[] { default, CreateValue() };
}

public class WrappedConverterCopierTests : CopierTester<WrapsMyForeignLibraryType, IDeepCopier<WrapsMyForeignLibraryType>>
{
    protected override WrapsMyForeignLibraryType CreateValue() => new() { IntValue = 12, ForeignValue = new MyForeignLibraryType(12, "hi", DateTimeOffset.Now), OtherIntValue = 7468249 };
    protected override bool Equals(WrapsMyForeignLibraryType left, WrapsMyForeignLibraryType right) => ReferenceEquals(left, right) || left.Equals(right);
    protected override WrapsMyForeignLibraryType[] TestValues => new WrapsMyForeignLibraryType[] { default, CreateValue() };
}

public class StructConverterCodecTests : ValueTypeFieldCodecTester<MyForeignLibraryValueType, IFieldCodec<MyForeignLibraryValueType>>
{
    protected override MyForeignLibraryValueType CreateValue() => new(12, "hi", DateTimeOffset.Now);
    protected override bool Equals(MyForeignLibraryValueType left, MyForeignLibraryValueType right) => left.Equals(right);
    protected override MyForeignLibraryValueType[] TestValues => new MyForeignLibraryValueType[] { default, CreateValue() };
}

public class StructConverterCopierTests : CopierTester<MyForeignLibraryValueType, IDeepCopier<MyForeignLibraryValueType>>
{
    protected override MyForeignLibraryValueType CreateValue() => new(12, "hi", DateTimeOffset.Now);
    protected override bool Equals(MyForeignLibraryValueType left, MyForeignLibraryValueType right) => left.Equals(right);
    protected override MyForeignLibraryValueType[] TestValues => new MyForeignLibraryValueType[] { default, CreateValue() };
}

public class WrappedStructConverterCodecTests : ValueTypeFieldCodecTester<WrapsMyForeignLibraryValueType, IFieldCodec<WrapsMyForeignLibraryValueType>>
{
    protected override WrapsMyForeignLibraryValueType CreateValue() => new() { IntValue = 12, ForeignValue = new MyForeignLibraryValueType(12, "hi", DateTimeOffset.Now), OtherIntValue = 7468249 };
    protected override bool Equals(WrapsMyForeignLibraryValueType left, WrapsMyForeignLibraryValueType right) => left.Equals(right);
    protected override WrapsMyForeignLibraryValueType[] TestValues => new WrapsMyForeignLibraryValueType[] { default, CreateValue() };
}

public class WrappedStructConverterCopierTests : CopierTester<WrapsMyForeignLibraryValueType, IDeepCopier<WrapsMyForeignLibraryValueType>>
{
    protected override WrapsMyForeignLibraryValueType CreateValue() => new() { IntValue = 12, ForeignValue = new MyForeignLibraryValueType(12, "hi", DateTimeOffset.Now), OtherIntValue = 7468249 };
    protected override bool Equals(WrapsMyForeignLibraryValueType left, WrapsMyForeignLibraryValueType right) => left.Equals(right);
    protected override WrapsMyForeignLibraryValueType[] TestValues => new WrapsMyForeignLibraryValueType[] { default, CreateValue() };
}

public class DerivedConverterCodecTests : FieldCodecTester<DerivedFromMyForeignLibraryType, IFieldCodec<DerivedFromMyForeignLibraryType>>
{
    protected override DerivedFromMyForeignLibraryType CreateValue() => new(658, 12, "hi", DateTimeOffset.Now);
    protected override bool Equals(DerivedFromMyForeignLibraryType left, DerivedFromMyForeignLibraryType right) => ReferenceEquals(left, right) || left.Equals(right);
    protected override DerivedFromMyForeignLibraryType[] TestValues => new DerivedFromMyForeignLibraryType[] { null, CreateValue() };
}

public class DerivedConverterCopierTests : CopierTester<DerivedFromMyForeignLibraryType, IDeepCopier<DerivedFromMyForeignLibraryType>>
{
    protected override DerivedFromMyForeignLibraryType CreateValue() => new(658, 12, "hi", DateTimeOffset.Now);
    protected override bool Equals(DerivedFromMyForeignLibraryType left, DerivedFromMyForeignLibraryType right) => ReferenceEquals(left, right) || left.Equals(right);
    protected override DerivedFromMyForeignLibraryType[] TestValues => new DerivedFromMyForeignLibraryType[] { null, CreateValue() };
}
