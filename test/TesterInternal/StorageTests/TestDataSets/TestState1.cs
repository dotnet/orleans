using System;
using System.Collections.Generic;


namespace UnitTests.StorageTests.Relational.TestDataSets
{
    /// <summary>
    /// A state used to test if saving, reading and clearing of the storage functions as expected.
    /// </summary>
    [Serializable]
    [Orleans.GenerateSerializer]
    public class TestState1: IEquatable<TestState1>
    {
        [Orleans.Id(0)]
        public string A { get; set; }

        [Orleans.Id(1)]
        public int B { get; set; }

        [Orleans.Id(2)]
        public long C { get; set; }

        public override bool Equals(object obj)
        {
            return Equals(obj as TestState1);
        }


        public bool Equals(TestState1 other)
        {
            if(ReferenceEquals(other, null))
            {
                return false;
            }

            return EqualityComparer<string>.Default.Equals(A, other.A) && B.Equals(other.B) && C.Equals(other.C);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 23 + EqualityComparer<string>.Default.GetHashCode(A);
                hash = hash * 23 + B.GetHashCode();
                hash = hash * 23 + C.GetHashCode();

                return hash;
            }
        }
    }
}
