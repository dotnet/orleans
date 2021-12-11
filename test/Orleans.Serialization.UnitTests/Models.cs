using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Orleans;

[GenerateSerializer]
public record Person([property: Id(0)] int Age, [property: Id(1)] string Name)
{
    [Id(2)]
    public string FavouriteColor { get; init; }

    [Id(3)]
    public string StarSign { get; init; }
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
    [GenerateSerializer]
    [WellKnownId(3201)]
    public class SomeClassWithSerializers
    {
        [Id(0)]
        public int IntProperty { get; set; }

        [Id(1)] public int IntField;

        public int UnmarkedField;

        public int UnmarkedProperty { get; set; }

        public override string ToString() => $"{nameof(IntField)}: {IntField}, {nameof(IntProperty)}: {IntProperty}";
    }

    [GenerateSerializer]
    [WellKnownAlias("sercla1")]
    public class SerializableClassWithCompiledBase : List<int>
    {
        [Id(0)]
        public int IntProperty { get; set; }
    }

    [GenerateSerializer]
    [WellKnownAlias("gpoco`1")]
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
}