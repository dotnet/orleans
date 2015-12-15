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
        public abstract int Int { get; set; }

        public List<ISomeInterface> Interfaces { get; set; }

        public List<SomeAbstractClass> Classes { get; set; }

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
        [Serializable]
        public class SomeConcreteClass : SomeAbstractClass
        {
            public override int Int { get; set; }

            public string String { get; set; }
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
}
