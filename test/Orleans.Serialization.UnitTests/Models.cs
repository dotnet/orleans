using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessagePack;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orleans;

[Alias("test.person.alias"), GenerateSerializer]
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

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class MyNewtonsoftJsonSerializableAttribute : Attribute
{
}

internal interface IMyBase
{
    MyValue BaseValue { get; set; }
}

internal interface IMySub : IMyBase
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
[SuppressReferenceTracking]
public class MySuppressReferenceTrackingValue : MyValue
{
    public MySuppressReferenceTrackingValue(int value) : base(value)
    {
    }
}

[GenerateSerializer]
public class MyCustomException : Exception
{
    public MyCustomException() { }
    public MyCustomException(string message) : base(message) { }
    public MyCustomException(string message, Exception inner) : base(message, inner) { }
#if NET8_0_OR_GREATER
    [Obsolete]
#endif
    public MyCustomException(SerializationInfo info, StreamingContext context) : base(info, context) { }

    [Id(0)]
    public int CustomInt;
}

public class MyCustomForeignException : Exception
{
    public MyCustomForeignException(int customInt)
    {
        CustomInt = customInt;
    }

#if NET8_0_OR_GREATER
    [Obsolete]
#endif
    public MyCustomForeignException(SerializationInfo info, StreamingContext context) : base(info, context) { }

    [Id(0)]
    public int CustomInt;
}

[GenerateSerializer]
public struct MyCustomForeignExceptionSurrogate
{
    [Id(0)]
    public int CustomInt { get; set; }

    public MyCustomForeignExceptionSurrogate(int customInt)
    {
        CustomInt = customInt;
    }
}

[RegisterConverter]
public sealed class MyCustomForeignExceptionSurrogateConverter : IConverter<MyCustomForeignException, MyCustomForeignExceptionSurrogate>
{
    public MyCustomForeignException ConvertFromSurrogate(in MyCustomForeignExceptionSurrogate surrogate) =>
        new MyCustomForeignException(surrogate.CustomInt);

    public MyCustomForeignExceptionSurrogate ConvertToSurrogate(in MyCustomForeignException value) =>
        new MyCustomForeignExceptionSurrogate(value.CustomInt);
}

[GenerateSerializer]
[SuppressMessage("Usage", "ORLEANS0004:Add missing serialization attributes", Justification = "Intentional for tests")]
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

        public override readonly bool Equals(object obj) =>
            obj is MyForeignLibraryValueType type
            && Num == type.Num
            && string.Equals(String, type.String, StringComparison.Ordinal)
            && DateTimeOffset.Equals(type.DateTimeOffset);

        public override readonly int GetHashCode() => HashCode.Combine(Num, String, DateTimeOffset);
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

        public override readonly bool Equals(object obj) => obj is WrapsMyForeignLibraryValueType type && IntValue == type.IntValue && EqualityComparer<MyForeignLibraryValueType>.Default.Equals(ForeignValue, type.ForeignValue) && OtherIntValue == type.OtherIntValue;
        public override readonly int GetHashCode() => HashCode.Combine(IntValue, ForeignValue, OtherIntValue);
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

    [MyNewtonsoftJsonSerializable]
    public class MyNewtonsoftJsonClass : MyNonJsonBaseClass, IEquatable<MyNewtonsoftJsonClass>
    {
        [JsonProperty]
        public string SubTypeProperty { get; set; }

        [JsonProperty]
        public TestId Id { get; set; }

        [JsonProperty]
        public JArray JsonArray { get; set; } = new JArray(true, 42, "hello");

        [JsonProperty]
        public JObject JsonObject { get; set; } = new() { ["foo"] = "bar" };

        public override string ToString() => $"{nameof(SubTypeProperty)}: {SubTypeProperty}, {base.ToString()}";
        public bool Equals(MyNewtonsoftJsonClass other) => other is not null && base.Equals(other) && string.Equals(SubTypeProperty, other.SubTypeProperty, StringComparison.Ordinal) && EqualityComparer<TestId>.Default.Equals(Id, other.Id)
            && string.Equals(JsonConvert.SerializeObject(JsonArray), JsonConvert.SerializeObject(other.JsonArray))
            && string.Equals(JsonConvert.SerializeObject(JsonObject), JsonConvert.SerializeObject(other.JsonObject));
        public override bool Equals(object obj) => Equals(obj as MyJsonClass);
        public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), SubTypeProperty);
    }

    [MyJsonSerializable]
    public class MyJsonClass : MyNonJsonBaseClass, IEquatable<MyJsonClass>
    {
        [JsonProperty]
        public string SubTypeProperty { get; set; }

        [JsonProperty]
        public TestId Id { get; set; }

        [JsonProperty]
        public JsonArray JsonArray { get; set; } = new JsonArray(true, 42, "hello");

        [JsonProperty]
        public JsonObject JsonObject { get; set; } = new() { ["foo"] = "bar" };

        public override string ToString() => $"{nameof(SubTypeProperty)}: {SubTypeProperty}, {base.ToString()}";
        public bool Equals(MyJsonClass other) => other is not null && base.Equals(other) && string.Equals(SubTypeProperty, other.SubTypeProperty, StringComparison.Ordinal) && EqualityComparer<TestId>.Default.Equals(Id, other.Id)
            && string.Equals(System.Text.Json.JsonSerializer.Serialize(JsonArray), System.Text.Json.JsonSerializer.Serialize(other.JsonArray))
            && string.Equals(System.Text.Json.JsonSerializer.Serialize(JsonObject), System.Text.Json.JsonSerializer.Serialize(other.JsonObject));
        public override bool Equals(object obj) => Equals(obj as MyJsonClass);
        public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), SubTypeProperty);
    }

    [System.Text.Json.Serialization.JsonConverter(typeof(StronglyTypedIdJsonConverter<TestId, Guid>))]
    public record TestId(Guid Id) : StronglyTypedId<Guid>(Id);

    public abstract record StronglyTypedId<TValue>(TValue Value)
        where TValue : notnull
    {
        public override string ToString() => Value.ToString() ?? typeof(TValue).ToString();
    }

    file class StronglyTypedIdJsonConverter<TStronglyTypedId, TValue> : System.Text.Json.Serialization.JsonConverter<TStronglyTypedId>
        where TStronglyTypedId : StronglyTypedId<TValue>
        where TValue : notnull
    {
        public override TStronglyTypedId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType is JsonTokenType.Null)
            {
                return null;
            }

            var value = System.Text.Json.JsonSerializer.Deserialize<TValue>(ref reader, options);
            var factory = StronglyTypedIdHelper.GetFactory<TValue>(typeToConvert);
            return (TStronglyTypedId)factory(value);
        }

        public override void Write(Utf8JsonWriter writer, TStronglyTypedId value, JsonSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                System.Text.Json.JsonSerializer.Serialize(writer, value.Value, options);
            }
        }
    }

    file static class StronglyTypedIdHelper
    {
        private static readonly ConcurrentDictionary<Type, Delegate> StronglyTypedIdFactories = new();

        public static Func<TValue, object> GetFactory<TValue>(Type stronglyTypedIdType)
            where TValue : notnull
        {
            return (Func<TValue, object>)StronglyTypedIdFactories.GetOrAdd(
                stronglyTypedIdType,
                CreateFactory<TValue>);
        }

        private static Func<TValue, object> CreateFactory<TValue>(Type stronglyTypedIdType)
            where TValue : notnull
        {
            if (!IsStronglyTypedId(stronglyTypedIdType))
            {
                throw new ArgumentException($"Type '{stronglyTypedIdType}' is not a strongly-typed id type", nameof(stronglyTypedIdType));
            }

            var ctor = stronglyTypedIdType.GetConstructor(new[] { typeof(TValue) });
            if (ctor is null)
            {
                throw new ArgumentException($"Type '{stronglyTypedIdType}' doesn't have a constructor with one parameter of type '{typeof(TValue)}'", nameof(stronglyTypedIdType));
            }

            var param = Expression.Parameter(typeof(TValue), "value");
            var body = Expression.New(ctor, param);
            var lambda = Expression.Lambda<Func<TValue, object>>(body, param);
            return lambda.Compile();
        }

        public static bool IsStronglyTypedId(Type type) => IsStronglyTypedId(type, out _);

        public static bool IsStronglyTypedId(Type type, out Type idType)
        {
            if (type is null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            if (type.BaseType is Type baseType &&
                baseType.IsGenericType &&
                baseType.GetGenericTypeDefinition() == typeof(StronglyTypedId<>))
            {
                idType = baseType.GetGenericArguments()[0];
                return true;
            }

            idType = null;
            return false;
        }
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

    [GenerateSerializer(GenerateFieldIds = GenerateFieldIds.PublicProperties)]
    public class PocoWithAutogeneratedIds : IEquatable<PocoWithAutogeneratedIds>
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

        public override bool Equals(object obj) => Equals(obj as PocoWithAutogeneratedIds);
        public bool Equals(PocoWithAutogeneratedIds other) => other is not null
            && A == other.A
            && B == other.B
            && C == other.C
            && D == other.D
            && E == other.E
            && F == other.F
            && G == other.G
            && H == other.H
            && I == other.I
            && J == other.J
            && K == other.K;

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(A);
            hash.Add(B);
            hash.Add(C);
            hash.Add(D);
            hash.Add(E);
            hash.Add(F);
            hash.Add(G);
            hash.Add(H);
            hash.Add(I);
            hash.Add(J);
            hash.Add(K);
            return hash.ToHashCode();
        }

        public static bool operator ==(PocoWithAutogeneratedIds left, PocoWithAutogeneratedIds right) => EqualityComparer<PocoWithAutogeneratedIds>.Default.Equals(left, right);
        public static bool operator !=(PocoWithAutogeneratedIds left, PocoWithAutogeneratedIds right) => !(left == right);
    }

    [GenerateSerializer]
    [Alias("sercla1")]
    public class SerializableClassWithCompiledBase : List<int>
    {
        [Id(0)]
        public int IntProperty { get; set; }
    }

#if NET6_0_OR_GREATER
    [GenerateSerializer]
    public class ClassWithRequiredMembers
    {
        [Id(0)]
        public required int IntProperty { get; set; }

        [Id(1)]
        public required string StringField;
    }

    [GenerateSerializer]
    public class SubClassWithRequiredMembersInBase : ClassWithRequiredMembers
    {
    }
#endif

    [GenerateSerializer]
    public sealed class DerivedFromDictionary<TKey, TValue> : Dictionary<TKey, TValue>
    {
        public DerivedFromDictionary(IEqualityComparer<TKey> comparer) : base(comparer)
        {
        }

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
    [Alias("GenericPocoWithConstraint`2")]
    public class GenericPocoWithConstraint<TClass, TStruct>
        : GenericPoco<TStruct> where TClass : List<int>, new() where TStruct : struct
    {
        [Id(0)]
        public new TClass Field { get; set; }

        [Id(999)]
        public TStruct ValueField { get; set; }
    }

    public sealed class Outer<T>
    {
        [GenerateSerializer]
        [Alias("Orleans.Serialization.UnitTests.Outer.InnerNonGen`1")]
        public class InnerNonGen
        {
        }

        [GenerateSerializer]
        [Alias("Orleans.Serialization.UnitTests.Outer.InnerGen`2")]
        public class InnerGen<U>
        {
        }
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
    [SuppressMessage("Usage", "ORLEANS0004:Add missing serialization attributes", Justification = "Intentional for tests")]
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
        public readonly int GetIntField() => _intField;

        public override readonly string ToString() => $"{nameof(_intField)}: {_intField}, {nameof(IntProperty)}: {IntProperty}";
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
        [NonSerialized]
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

    public class MyFirstForeignLibraryType
    {

        public int Num { get; set; }
        public string String { get; set; }
        public DateTimeOffset DateTimeOffset { get; set; }

        public override bool Equals(object obj) =>
            obj is MyFirstForeignLibraryType type
            && Num == type.Num
            && string.Equals(String, type.String, StringComparison.Ordinal)
            && DateTimeOffset.Equals(type.DateTimeOffset);

        public override int GetHashCode() => HashCode.Combine(Num, String, DateTimeOffset);
    }

    public class MySecondForeignLibraryType
    {
        public string Name { get; set; }
        public float Value { get; set; }
        public DateTimeOffset Timestamp { get; set; }

        public override bool Equals(object obj) =>
            obj is MySecondForeignLibraryType type
            && string.Equals(Name, type.Name, StringComparison.Ordinal)
            && Value == type.Value
            && Timestamp.Equals(type.Timestamp);

        public override int GetHashCode() => HashCode.Combine(Name, Value, Timestamp);
    }

    [GenerateSerializer]
    public struct MyFirstForeignLibraryTypeSurrogate
    {
        [Id(0)]
        public int Num { get; set; }

        [Id(1)]
        public string String { get; set; }

        [Id(2)]
        public DateTimeOffset DateTimeOffset { get; set; }
    }


    [GenerateSerializer]
    public struct MySecondForeignLibraryTypeSurrogate
    {
        [Id(0)]
        public string Name { get; set; }

        [Id(1)]
        public float Value { get; set; }

        [Id(2)]
        public DateTimeOffset Timestamp { get; set; }
    }

    [RegisterConverter]
    public sealed class MyCombinedForeignLibraryValueTypeSurrogateConverter :
        IConverter<MyFirstForeignLibraryType, MyFirstForeignLibraryTypeSurrogate>,
        IConverter<MySecondForeignLibraryType, MySecondForeignLibraryTypeSurrogate>
    {
        public MyFirstForeignLibraryType ConvertFromSurrogate(in MyFirstForeignLibraryTypeSurrogate surrogate)
            => new() { Num = surrogate.Num, String = surrogate.String, DateTimeOffset = surrogate.DateTimeOffset };
        public MyFirstForeignLibraryTypeSurrogate ConvertToSurrogate(in MyFirstForeignLibraryType value)
            => new() { Num = value.Num, String = value.String, DateTimeOffset = value.DateTimeOffset };

        public MySecondForeignLibraryType ConvertFromSurrogate(in MySecondForeignLibraryTypeSurrogate surrogate)
            => new() { Name = surrogate.Name, Value = surrogate.Value, Timestamp = surrogate.Timestamp };
        public MySecondForeignLibraryTypeSurrogate ConvertToSurrogate(in MySecondForeignLibraryType value)
            => new() { Name = value.Name, Value = value.Value, Timestamp = value.Timestamp };
    }

    [MessagePackObject]
    public sealed record MyMessagePackClass
    {
        [Key(0)]
        public int IntProperty { get; init; }

        [Key(1)]
        public string StringProperty { get; init; }

        [Key(2)]
        public MyMessagePackSubClass SubClass { get; init; }

        [Key(3)]
        public IMyMessagePackUnion Union { get; init; }
    }

    [MessagePackObject]
    public sealed record MyMessagePackSubClass
    {
        [Key(0)]
        public Guid Id { get; init; }
    }

    [Union(0, typeof(MyMessagePackUnionVariant1))]
    [Union(1, typeof(MyMessagePackUnionVariant2))]
    public interface IMyMessagePackUnion
    {
    }

    [MessagePackObject]
    public sealed record MyMessagePackUnionVariant1 : IMyMessagePackUnion
    {
        [Key(0)]
        public int IntProperty { get; init; }
    }

    [MessagePackObject]
    public sealed record MyMessagePackUnionVariant2 : IMyMessagePackUnion
    {
        [Key(0)]
        public string StringProperty { get; init; }
    }
}
