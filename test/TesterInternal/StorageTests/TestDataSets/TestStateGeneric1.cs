using System;
using System.Collections.Generic;


namespace UnitTests.StorageTests.Relational.TestDataSets
{
    /// <summary>
    /// A generic state used to test if saving, reading and clearing of the storage functions as expected.
    /// </summary>
    [Serializable]
    [Orleans.GenerateSerializer]
    public class TestStateGeneric1<T>: IEquatable<TestStateGeneric1<T>>
    {
        [Orleans.Id(0)]
        public T SomeData { get; set; }

        [Orleans.Id(1)]
        public string A { get; set; }

        [Orleans.Id(2)]
        public int B { get; set; }

        [Orleans.Id(3)]
        public long C { get; set; }


        public override bool Equals(object obj)
        {
            return Equals(obj as TestStateGeneric1<T>);
        }


        public bool Equals(TestStateGeneric1<T> other)
        {
            if(ReferenceEquals(other, null))
            {
                return false;
            }

            return EqualityComparer<T>.Default.Equals(SomeData, other.SomeData)
                && EqualityComparer<string>.Default.Equals(A, other.A)
                && B.Equals(other.B)
                && C.Equals(other.C);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 23 + EqualityComparer<T>.Default.GetHashCode(SomeData);
                hash = hash * 23 + EqualityComparer<string>.Default.GetHashCode(A);
                hash = hash * 23 + B.GetHashCode();
                hash = hash * 23 + C.GetHashCode();

                return hash;
            }
        }
    }
}
