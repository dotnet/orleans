using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FSharp.Core;
using Newtonsoft.Json;
using Orleans;
using Orleans.Serialization.UnitTests;

[GenerateSerializer]
public record Person([property: Id(0)] int Age, [property: Id(1)] string Name)
{
    [Id(2)]
    public string FavouriteColor { get; init; }

    [Id(3)]
    public string StarSign { get; init; }
}

[GenerateSerializer]
public record Person2(int Age, string Name)
{
    [Id(0)]
    public string FavouriteColor { get; init; }

    [Id(1)]
    public string StarSign { get; init; }
}

[GenerateSerializer(IncludePrimaryConstructorParameters = false)]
public record Person3(int Age, string Name)
{
    [Id(0)]
    public string FavouriteColor { get; init; }

    [Id(1)]
    public string StarSign { get; init; }
}

[GenerateSerializer]
public record Person4(int Age, string Name);

[GenerateSerializer(IncludePrimaryConstructorParameters = false)]
public record Person5([property: Id(0)] int Age, [property: Id(1)] string Name)
{
    [Id(2)]
    public string FavouriteColor { get; init; }

    [Id(3)]
    public string StarSign { get; init; }
}

[GenerateSerializer]
public class Person5_Class
{
    [Id(0)] public int Age { get; init; }
    [Id(1)] public string Name { get; init; }
    [Id(2)] public string FavouriteColor { get; init; }
    [Id(3)] public string StarSign { get; init; }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class MyJsonSerializableAttribute : Attribute
{
}

interface IMyBase
{
    MyValue BaseValue { get; set; }
}

interface IMySub : IMyBase
{
    MyValue SubValue { get; set; }
}

[GenerateSerializer]
public class MyValue : IEquatable<MyValue>
{
    [Id(0)]
    public int Value { get; set; }

    public MyValue(int value) => Value = value;

    public static implicit operator int(MyValue value) => value.Value;
    public static implicit operator MyValue(int value) => new(value);

    public bool Equals(MyValue other)
    {
        return other is not null && Value == other.Value;
    }

    public override bool Equals(object obj)
    {
        return Equals(obj as MyValue);
    }

    public override int GetHashCode() => Value;
} 

[GenerateSerializer]
[Immutable]
public class MyImmutableBase : IMyBase
{
    [Id(0)]
    public MyValue BaseValue { get; set; }
}

[GenerateSerializer]
public sealed class MyMutableSub : MyImmutableBase, IMySub
{
    [Id(0)]
    public MyValue SubValue { get; set; }
}

[GenerateSerializer]
[Immutable]
public sealed class MyImmutableSub : MyImmutableBase, IMySub
{
    [Id(0)]
    public MyValue SubValue { get; set; }
}

[GenerateSerializer]
public class MyMutableBase : IMyBase
{
    [Id(0)]
    public MyValue BaseValue { get; set; }
}

[GenerateSerializer]
public sealed class MySealedSub : MyMutableBase, IMySub
{
    [Id(0)]
    public MyValue SubValue { get; set; }
}

[GenerateSerializer]
[Immutable]
public sealed class MySealedImmutableSub : MyMutableBase, IMySub
{
    [Id(0)]
    public MyValue SubValue { get; set; }
}

[GenerateSerializer]
[Immutable]
public class MyUnsealedImmutableSub : MyMutableBase, IMySub
{
    [Id(0)]
    public MyValue SubValue { get; set; }
}

[GenerateSerializer]
public class SomeClassWithSerializers
{
    [Id(0)]
    public int IntProperty { get; set; }

    [Id(1)] public int IntField;

    public int UnmarkedField;

    public int UnmarkedProperty { get; set; }

    public override string ToString() => $"{nameof(IntField)}: {IntField}, {nameof(IntProperty)}: {IntProperty}";
}

namespace Orleans.Serialization.UnitTests
{
    public class MyForeignLibraryType
    {
        public MyForeignLibraryType() { }

        public MyForeignLibraryType(int num, string str, DateTimeOffset dto)
        {
            Num = num;
            String = str;
            DateTimeOffset = dto;
        }

        public int Num { get; set; }
        public string String { get; set; }
        public DateTimeOffset DateTimeOffset { get; set; }

        public override bool Equals(object obj) =>
            obj is MyForeignLibraryType type
            && Num == type.Num
            && string.Equals(String, type.String, StringComparison.Ordinal)
            && DateTimeOffset.Equals(type.DateTimeOffset);

        public override int GetHashCode() => HashCode.Combine(Num, String, DateTimeOffset);
    }

    [GenerateSerializer]
    public struct MyForeignLibraryTypeSurrogate
    {
        [Id(0)]
        public int Num { get; set; }

        [Id(1)]
        public string String { get; set; }

        [Id(2)]
        public DateTimeOffset DateTimeOffset { get; set; }
    }

    [RegisterConverter]
    public sealed class MyForeignLibraryTypeSurrogateConverter : IConverter<MyForeignLibraryType, MyForeignLibraryTypeSurrogate>, IPopulator<MyForeignLibraryType, MyForeignLibraryTypeSurrogate>
    {
        public MyForeignLibraryType ConvertFromSurrogate(in MyForeignLibraryTypeSurrogate surrogate)
            => new(surrogate.Num, surrogate.String, surrogate.DateTimeOffset);

        public MyForeignLibraryTypeSurrogate ConvertToSurrogate(in MyForeignLibraryType value)
            => new() { Num = value.Num, String = value.String, DateTimeOffset = value.DateTimeOffset };
        public void Populate(in MyForeignLibraryTypeSurrogate surrogate, MyForeignLibraryType value)
        {
            value.Num = surrogate.Num;
            value.String = surrogate.String;
            value.DateTimeOffset = surrogate.DateTimeOffset;
        }
    }

    [GenerateSerializer]
    public class WrapsMyForeignLibraryType
    {
        [Id(0)]
        public int IntValue { get; set; }

        [Id(1)]
        public MyForeignLibraryType ForeignValue { get; set; }

        [Id(2)]
        public int OtherIntValue { get; set; }

        public override bool Equals(object obj) => obj is WrapsMyForeignLibraryType type && IntValue == type.IntValue && EqualityComparer<MyForeignLibraryType>.Default.Equals(ForeignValue, type.ForeignValue) && OtherIntValue == type.OtherIntValue;
        public override int GetHashCode() => HashCode.Combine(IntValue, ForeignValue, OtherIntValue);
    }

    [GenerateSerializer]
    public class DerivedFromMyForeignLibraryType : MyForeignLibraryType
    {
        public DerivedFromMyForeignLibraryType() { }
        public DerivedFromMyForeignLibraryType(int intValue, int num, string str, DateTimeOffset dto) : base(num, str, dto)
        {
            IntValue = intValue;
        }

        [Id(0)]
        public int IntValue { get; set; }

        public override bool Equals(object obj) => obj is DerivedFromMyForeignLibraryType type && base.Equals(obj) && Num == type.Num && String == type.String && DateTimeOffset.Equals(type.DateTimeOffset) && IntValue == type.IntValue;
        public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), Num, String, DateTimeOffset, IntValue);
    }

    public struct MyForeignLibraryValueType
    {
        public MyForeignLibraryValueType(int num, string str, DateTimeOffset dto)
        {
            Num = num;
            String = str;
            DateTimeOffset = dto;
        }

        public int Num { get; }
        public string String { get; }
        public DateTimeOffset DateTimeOffset { get; }

        public override bool Equals(object obj) =>
            obj is MyForeignLibraryValueType type
            && Num == type.Num
            && string.Equals(String, type.String, StringComparison.Ordinal)
            && DateTimeOffset.Equals(type.DateTimeOffset);

        public override int GetHashCode() => HashCode.Combine(Num, String, DateTimeOffset);
    }

    [GenerateSerializer]
    public struct MyForeignLibraryValueTypeSurrogate
    {
        [Id(0)]
        public int Num { get; set; }

        [Id(1)]
        public string String { get; set; }

        [Id(2)]
        public DateTimeOffset DateTimeOffset { get; set; }
    }

    [RegisterConverter]
    public sealed class MyForeignLibraryValueTypeSurrogateConverter : IConverter<MyForeignLibraryValueType, MyForeignLibraryValueTypeSurrogate>
    {
        public MyForeignLibraryValueType ConvertFromSurrogate(in MyForeignLibraryValueTypeSurrogate surrogate)
            => new(surrogate.Num, surrogate.String, surrogate.DateTimeOffset);

        public MyForeignLibraryValueTypeSurrogate ConvertToSurrogate(in MyForeignLibraryValueType value)
            => new() { Num = value.Num, String = value.String, DateTimeOffset = value.DateTimeOffset };
    }

    [GenerateSerializer]
    public struct WrapsMyForeignLibraryValueType
    {
        [Id(0)]
        public int IntValue { get; set; }

        [Id(1)]
        public MyForeignLibraryValueType ForeignValue { get; set; }

        [Id(2)]
        public int OtherIntValue { get; set; }

        public override bool Equals(object obj) => obj is WrapsMyForeignLibraryValueType type && IntValue == type.IntValue && EqualityComparer<MyForeignLibraryValueType>.Default.Equals(ForeignValue, type.ForeignValue) && OtherIntValue == type.OtherIntValue;
        public override int GetHashCode() => HashCode.Combine(IntValue, ForeignValue, OtherIntValue);
    }

    [GenerateSerializer]
    public class MyNonJsonBaseClass : IEquatable<MyNonJsonBaseClass>
    {
        [Id(0)]
        [JsonProperty]
        public int IntProperty { get; set; }

        public override string ToString() => $"{nameof(IntProperty)}: {IntProperty}";
        public bool Equals(MyNonJsonBaseClass other) => other is not null && (ReferenceEquals(this, other) || other.IntProperty == IntProperty);
        public override bool Equals(object obj) => Equals(obj as MyNonJsonBaseClass);
        public override int GetHashCode() => HashCode.Combine(IntProperty);
    }

    [MyJsonSerializable]
    public class MyJsonClass : MyNonJsonBaseClass, IEquatable<MyJsonClass>
    {
        [JsonProperty]
        public string SubTypeProperty { get; set; }

        public override string ToString() => $"{nameof(SubTypeProperty)}: {SubTypeProperty}, {base.ToString()}";
        public bool Equals(MyJsonClass other) => other is not null && base.Equals(other) && string.Equals(SubTypeProperty, other.SubTypeProperty, StringComparison.Ordinal);
        public override bool Equals(object obj) => Equals(obj as MyJsonClass);
        public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), SubTypeProperty);
    }

    [GenerateSerializer]
    public enum MyCustomEnum
    {
        None,
        One,
        Two,
        Three
    }

    [GenerateSerializer]
    public class RecursiveClass
    {
        [Id(0)]
        public int IntProperty { get; set; }

        [Id(1)]
        public RecursiveClass RecursiveProperty { get; set; }
    }

    [GenerateSerializer]
    [Id(3201)]
    public class SomeClassWithSerializers
    {
        [Id(0)]
        public int IntProperty { get; set; }

        [Id(1)] public int IntField;

        [Id(2)]
        public object OtherObject { get; set; }

        [NonSerialized]
        public int UnmarkedField;

        [field: NonSerialized]
        public int UnmarkedProperty { get; set; }

        public override string ToString() => $"{nameof(IntField)}: {IntField}, {nameof(IntProperty)}: {IntProperty}";
    }

    [GenerateSerializer]
    [Alias("sercla1")]
    public class SerializableClassWithCompiledBase : List<int>
    {
        [Id(0)]
        public int IntProperty { get; set; }
    }

    [GenerateSerializer]
    [Alias("gpoco`1")]
    public class GenericPoco<T>
    {
        [Id(0)]
        public T Field { get; set; }

        [Id(1030)]
        public T[] ArrayField { get; set; }
    }

    [GenerateSerializer]
    public class GenericPocoWithConstraint<TClass, TStruct>
        : GenericPoco<TStruct> where TClass : List<int>, new() where TStruct : struct
    {
        [Id(0)]
        public new TClass Field { get; set; }

        [Id(999)]
        public TStruct ValueField { get; set; }
    }

    [GenerateSerializer]
    public class ArrayPoco<T>
    {
        [Id(0)]
        public T[] Array { get; set; }

        [Id(1)]
        public T[,] Dim2 { get; set; }

        [Id(2)]
        public T[,,] Dim3 { get; set; }

        [Id(3)]
        public T[,,,] Dim4 { get; set; }

        [Id(4)]
        public T[,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,] Dim32 { get; set; }

        [Id(5)]
        public T[][] Jagged { get; set; }
    }

    [GenerateSerializer]
    public class ImmutableClass
    {
        public ImmutableClass(int intProperty, int intField, int unmarkedField, int unmarkedProperty)
        {
            IntProperty = intProperty;
            _intField = intField;
            UnmarkedField = unmarkedField;
            UnmarkedProperty = unmarkedProperty;
        }

        [Id(0)]
        public int IntProperty { get; }

        [Id(1)] private readonly int _intField;

        public int GetIntField() => _intField;

        public readonly int UnmarkedField;

        public int UnmarkedProperty { get; }

        public override string ToString() => $"{nameof(_intField)}: {_intField}, {nameof(IntProperty)}: {IntProperty}";
    }

    [GenerateSerializer]
    public struct ImmutableStruct
    {
        public ImmutableStruct(int intProperty, int intField)
        {
            IntProperty = intProperty;
            _intField = intField;
        }

        [Id(0)]
        public int IntProperty { get; }

        [Id(1)] private readonly int _intField;
        public int GetIntField() => _intField;

        public override string ToString() => $"{nameof(_intField)}: {_intField}, {nameof(IntProperty)}: {IntProperty}";
    }

    [GenerateSerializer]
    public class SystemCollectionsClass
    {
        [Id(0)]
        public HashSet<string> hashSetField;

        [Id(1)]
        public HashSet<string> HashSetProperty { get; set; }

        [Id(2)]
        public ConcurrentQueue<int> concurrentQueueField;

        [Id(3)]
        public ConcurrentQueue<int> ConcurrentQueueProperty { get; set; }

        [Id(4)]
        public ConcurrentDictionary<string, int> concurrentDictField;

        [Id(5)]
        public ConcurrentDictionary<string, int> ConcurrentDictProperty { get; set; }
    }

    [GenerateSerializer]
    public class ClassWithLargeCollectionAndUri
    {
        [Id(0)]
        public List<string> LargeCollection;

        [Id(1)]
        public Uri Uri;
    }

    [GenerateSerializer]
    public class ClassWithManualSerializableProperty
    {
        private string _stringPropertyValue;

        [Id(0)]
        public Guid GuidProperty { get; set; }

        [Id(1)]
        public string StringProperty
        {
            get
            {
                return _stringPropertyValue ?? GuidProperty.ToString("N");
            }

            set
            {
                _stringPropertyValue = value;
                GuidProperty = Guid.TryParse(value, out var guidValue) ? guidValue : default;
            }
        }
    }

    [GenerateSerializer(GenerateFieldIds = GenerateFieldIds.PublicProperties), Immutable]
    public class ClassWithImplicitFieldIds
    {
        public string StringValue { get; }
        public MyCustomEnum EnumValue { get; }

        [OrleansConstructor]
        public ClassWithImplicitFieldIds(string stringValue, MyCustomEnum enumValue)
        {
            StringValue = stringValue;
            EnumValue = enumValue;
        }

        public override string ToString() => $"{nameof(StringValue)}: {StringValue}, {nameof(EnumValue)}: {EnumValue}";
    }

    [GenerateSerializer]
    public sealed class ClassWithTypeFields
    {
        [Id(1)] public Type Type1;
        [Id(2)] public object UntypedValue;
        [Id(3)] public Type Type2;
    }
}