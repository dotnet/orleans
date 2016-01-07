using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime.Configuration;

namespace UnitTests.GrainInterfaces
{
    [Serializable]
    public struct ValueTypeTestData
    {
        private readonly int intValue;

        public ValueTypeTestData(int i)
        {
            intValue = i;
        }

        public int GetValue()
        {
            return intValue;
        }
    }

    [Serializable]
    public enum TestEnum : byte 
    {
        First,
        Second,
        Third
    }

    [Serializable]
    public enum CampaignEnemyTestType : sbyte
    {
        None = -1,
        Brute = 0,
        Enemy1,
        Enemy2,
        Enemy3
    }

    [Serializable]
    public class ClassWithEnumTestData
    {
        public TestEnum EnumValue { get; set; }
        public CampaignEnemyTestType Enemy { get; set; }
    }

    [Serializable]
    public class LargeTestData
    {
        public string TestString { get; set; }
        private readonly bool[] boolArray;
        protected Dictionary<string, int> stringIntDict;
        public TestEnum EnumValue { get; set; }
        private readonly ClassWithEnumTestData[] classArray;
        public string Description { get; set; }

        public LargeTestData()
        {
            boolArray = new bool[20];
            stringIntDict = new Dictionary<string, int>();
            classArray = new ClassWithEnumTestData[50];
            for (var i = 0; i < 50; i++)
            {
                classArray[i] = new ClassWithEnumTestData();
            }
        }

        public void SetBit(int n)
        {
            boolArray[n] = true;
        }

        public void SetEnemy(int n, CampaignEnemyTestType enemy)
        {
            classArray[n].Enemy = enemy;
        }

        // This class is not actually used anywhere. It is here to test that the serializer generator properly handles
        // nested generic classes. If it doesn't, then the generated serializer for this class will fail to compile.
        [Serializable]
        public class NestedGeneric<T>
        {
            private T myT;
            private string s;

            public NestedGeneric(T t)
            {
                myT = t;
                s = myT.ToString();
            }

            public override string ToString()
            {
                return s;
            }

            public void SetT(T t)
            {
                myT = t;
                s = myT.ToString();
            }
        }
    }

    public interface IValueTypeTestGrain : IGrainWithIntegerKey
    {
        Task<ValueTypeTestData> GetStateData();

        Task SetStateData(ValueTypeTestData d);

        Task<CampaignEnemyTestType> GetEnemyType();
    }

    public interface IEnumResultGrain : IGrainWithIntegerKey
    {
        Task<CampaignEnemyTestType> GetEnemyType();

        Task<ClusterConfiguration> GetConfiguration();
    }

    [Serializable]
    [Immutable]
    public class ImmutableType
    {
        private readonly int a;
        private readonly int b;

        public int A { get { return a; } }
        public int B { get { return b; } }

        public ImmutableType(int aval, int bval)
        {
            a = aval;
            b = bval;
        }
    }

    [Serializable]
    public class EmbeddedImmutable
    {
        public string A { get; set; }
        private readonly Immutable<List<int>> list;
        public Immutable<List<int>> B { get { return list; } }

        public EmbeddedImmutable(string a, params int[] listOfInts)
        {
            A = a;
            var l = new List<int>();
            l.AddRange(listOfInts);
            list = new Immutable<List<int>>(l);
        }
    }
}
