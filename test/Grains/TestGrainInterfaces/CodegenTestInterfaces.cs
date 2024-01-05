using System.Collections;

namespace UnitTests.GrainInterfaces
{
    using Orleans.Concurrency;

    namespace One
    {
        [GenerateSerializer]
        public class Command
        {
        }
    }

    namespace Two
    {
        [GenerateSerializer]
        public class Command
        {
        }
    }

    /// <summary>
    /// Repro for https://github.com/dotnet/orleans/issues/3713.
    /// Having multiple methods with the same name and same parameter type
    /// name would cause a code generation failure because only one of the
    /// methods would be implemented in the generated GrainReference.
    /// </summary>
    internal interface ISameNameParameterTypeGrain : IGrainWithIntegerKey
    {
        Task ExecuteCommand(One.Command command);
        Task ExecuteCommand(Two.Command command);
    }

    internal interface IInternalPingGrain : IGrainWithIntegerKey
    {
        Task Ping();
    }

    public interface ISomeGrain : IGrainWithIntegerKey
    {
        Task Do(Outsider o);
    }

    [Unordered]
    public interface ISomeGrainWithInvocationOptions : IGrainWithIntegerKey
    {
        [AlwaysInterleave]
        Task AlwaysInterleave();
    }

    public interface ISerializationGenerationGrain : IGrainWithIntegerKey
    {
        Task<object> RoundTripObject(object input);
        Task<SomeStruct> RoundTripStruct(SomeStruct input);
        Task<SomeAbstractClass> RoundTripClass(SomeAbstractClass input);
        Task<ISomeInterface> RoundTripInterface(ISomeInterface input);
        Task<SomeAbstractClass.SomeEnum> RoundTripEnum(SomeAbstractClass.SomeEnum input);

        Task SetState(SomeAbstractClass input);
        Task<SomeAbstractClass> GetState();
    }
}

[GenerateSerializer]
public class Outsider { }

namespace UnitTests.GrainInterfaces
{
    [Serializable]
    [GenerateSerializer]
    public class CaseInsensitiveStringEquality : EqualityComparer<string>
    {
        public override bool Equals(string x, string y)
        {
            return x.Equals(y, StringComparison.OrdinalIgnoreCase);
        }

        public override int GetHashCode(string obj)
        {
            return obj.ToLowerInvariant().GetHashCode();
        }
    }

    [Serializable]
    [GenerateSerializer]
    public class Mod5IntegerComparer : EqualityComparer<int>
    {
        public override bool Equals(int x, int y)
        {
            return ((x - y) % 5) == 0;
        }

        public override int GetHashCode(int obj)
        {
            return obj % 5;
        }
    }

    [Serializable]
    [GenerateSerializer]
    public class CaseInsensitiveStringComparer : Comparer<string>
    {
        public override int Compare(string x, string y)
        {
            var x1 = x.ToLowerInvariant();
            var y1 = y.ToLowerInvariant();
            return Comparer<string>.Default.Compare(x1, y1);
        }
    }

    [Serializable]
    [GenerateSerializer]
    public class RootType
    {
        public RootType()
        {
            MyDictionary = new Dictionary<string, object>();
            MyDictionary.Add("obj1", new InnerType());
            MyDictionary.Add("obj2", new InnerType());
            MyDictionary.Add("obj3", new InnerType());
            MyDictionary.Add("obj4", new InnerType());
        }

        [Id(0)]
        public Dictionary<string, object> MyDictionary { get; set; }

        public override bool Equals(object obj)
        {
            var actual = obj as RootType;
            if (actual == null)
            {
                return false;
            }
            if (MyDictionary == null) return actual.MyDictionary == null;
            if (actual.MyDictionary == null) return false;

            var set1 = new HashSet<KeyValuePair<string, object>>(MyDictionary);
            var set2 = new HashSet<KeyValuePair<string, object>>(actual.MyDictionary);
            bool ret = set1.SetEquals(set2);
            return ret;
        }

        public override int GetHashCode()
        {
            return base.GetHashCode();
        }
    }

    [Serializable]
    [GenerateSerializer]
    public struct SomeStruct
    {
        [Id(0)]
        public Guid Id { get; set; }
        [Id(1)]
        public int PublicValue { get; set; }
        [Id(2)]
        public int ValueWithPrivateSetter { get; private set; }
        [Id(3)]
        public int ValueWithPrivateGetter { private get; set; }
        [Id(4)]
        private int PrivateValue { get; set; }

        [Id(5)]
        public readonly int ReadonlyField;

        [Id(6)]
        public IEchoGrain SomeGrainReference { get; set; }

        public SomeStruct(int readonlyField)
            : this()
        {
            this.ReadonlyField = readonlyField;
        }

        public readonly int GetValueWithPrivateGetter()
        {
            return this.ValueWithPrivateGetter;
        }

        public readonly int GetPrivateValue()
        {
            return this.PrivateValue;
        }

        public void SetPrivateValue(int value)
        {
            this.PrivateValue = value;
        }

        public void SetValueWithPrivateSetter(int value)
        {
            this.ValueWithPrivateSetter = value;
        }
    }

    public interface ISomeInterface { int Int { get; set; } }

    [Serializable]
    [GenerateSerializer]
    public abstract class SomeAbstractClass : ISomeInterface
    {
        [NonSerialized]
        private int _nonSerializedIntField;

        public abstract int Int { get; set; }

        [Id(1)]
        public List<ISomeInterface> Interfaces { get; set; }

        [Id(2)]
        public SomeAbstractClass[] Classes { get; set; }

        [Obsolete("This field should not be serialized", true)]
        [Id(3)]
        public int ObsoleteIntWithError { get; set; }

        [Obsolete("This field should be serialized")]
        [Id(4)]
        public int ObsoleteInt { get; set; }

        [Id(5)]
        public IEchoGrain SomeGrainReference { get; set; }
        
#pragma warning disable 618
        public int GetObsoleteInt() => this.ObsoleteInt;
        public void SetObsoleteInt(int value)
        {
            this.ObsoleteInt = value;
        }
#pragma warning restore 618

        [Id(6)]
        public SomeEnum Enum { get; set; }

        public int NonSerializedInt
        {
            get
            {
                return this._nonSerializedIntField;
            }

            set
            {
                this._nonSerializedIntField = value;
            }
        }

        [Serializable]
        public enum SomeEnum
        {
            None,

            Something,

            SomethingElse
        }
    }

    [Serializable]
    [GenerateSerializer]
    public class AnotherConcreteClass : SomeAbstractClass
    {
        [Id(0)]
        public override int Int { get; set; }

        [Id(1)]
        public string AnotherString { get; set; }
    }

    [Serializable]
    [GenerateSerializer]
    public class InnerType
    {
        public InnerType()
        {
            Id = Guid.NewGuid();
            Something = Id.ToString();
        }

        [Id(0)]
        public Guid Id { get; set; }
        [Id(1)]
        public string Something { get; set; }

        public override bool Equals(object obj)
        {
            var actual = obj as InnerType;
            if (actual == null)
            {
                return false;
            }
            return Id.Equals(actual.Id) && Equals(Something, actual.Something);
        }

        public override int GetHashCode()
        {
            return base.GetHashCode();
        }
    }

    [Serializable]
    [GenerateSerializer]
    public class ClassWithStructConstraint<T>
        where T : struct
    {
        [Id(0)]
        public T Value { get; set; }
    }

    // This class should not have a serializer generated for it, since the serializer would not be able to access
    // the nested private class.
    [Serializable]
    public class ClassWithNestedPrivateClassInListField
    {
        private readonly List<NestedPrivateClass> _coolBeans = new List<NestedPrivateClass>
        {
            new NestedPrivateClass()
        };

        public IEnumerable CoolBeans => this._coolBeans;

        private class NestedPrivateClass
        {
        }
    }

    /// <summary>
    /// Regression test for https://github.com/dotnet/orleans/issues/5243.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public readonly struct ReadOnlyStructWithReadOnlyArray
    {
#pragma warning disable IDE0032 // Use auto property
        [Id(0)]
        private readonly byte[] _value;
#pragma warning restore IDE0032 // Use auto property

        public ReadOnlyStructWithReadOnlyArray(byte[] value) => this._value = value;

        public byte[] Value => this._value;
    }
}
