using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;


namespace UnitTests.GrainInterfaces
{
    using Orleans.Concurrency;

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

public class Outsider { }

namespace UnitTests.GrainInterfaces
{
    [Serializable]
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
    public struct SomeStruct
    {
        public Guid Id { get; set; }
        public int PublicValue { get; set; }
        public int ValueWithPrivateSetter { get; private set; }
        public int ValueWithPrivateGetter { private get; set; }
        private int PrivateValue { get; set; }
        public readonly int ReadonlyField;

        public SomeStruct(int readonlyField)
            : this()
        {
            this.ReadonlyField = readonlyField;
        }

        public int GetValueWithPrivateGetter()
        {
            return this.ValueWithPrivateGetter;
        }

        public int GetPrivateValue()
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
    public abstract class SomeAbstractClass : ISomeInterface
    {
        [NonSerialized]
        private int nonSerializedIntField;

        public abstract int Int { get; set; }

        public List<ISomeInterface> Interfaces { get; set; }

        public SomeAbstractClass[] Classes { get; set; }

        [Obsolete("This field should not be serialized", true)]
        public int ObsoleteIntWithError { get; set; }

        [Obsolete("This field should be serialized")]
        public int ObsoleteInt { get; set; }
        

        #pragma warning disable 618
        public int GetObsoleteInt() => this.ObsoleteInt;
        public void SetObsoleteInt(int value)
        {
            this.ObsoleteInt = value;
        }
        #pragma warning restore 618

        public SomeEnum Enum { get; set; }

        public int NonSerializedInt
        {
            get
            {
                return this.nonSerializedIntField;
            }

            set
            {
                this.nonSerializedIntField = value;
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

    public class OuterClass
    {
        public static SomeConcreteClass GetPrivateClassInstance() => new PrivateConcreteClass(Guid.NewGuid());

        public static Type GetPrivateClassType() => typeof(PrivateConcreteClass);

        [Serializable]
        public class SomeConcreteClass : SomeAbstractClass
        {
            public override int Int { get; set; }

            public string String { get; set; }

            private PrivateConcreteClass secretPrivateClass;

            public void ConfigureSecretPrivateClass()
            {
                this.secretPrivateClass = new PrivateConcreteClass(Guid.NewGuid());
            }

            public bool AreSecretBitsIdentitcal(SomeConcreteClass other)
            {
                return other.secretPrivateClass?.Identity == this.secretPrivateClass?.Identity;
            }
        }

        [Serializable]
        private class PrivateConcreteClass : SomeConcreteClass
        {
            public PrivateConcreteClass(Guid identity)
            {
                this.Identity = identity;
            }

            public readonly Guid Identity;
        }
    }

    [Serializable]
    public class AnotherConcreteClass : SomeAbstractClass
    {
        public override int Int { get; set; }

        public string AnotherString { get; set; }
    }

    [Serializable]
    public class InnerType
    {
        public InnerType()
        {
            Id = Guid.NewGuid();
            Something = Id.ToString();
        }
        public Guid Id { get; set; }
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
    public class ClassWithStructConstraint<T>
        where T : struct
    {
        public T Value { get; set; }
    }
}
