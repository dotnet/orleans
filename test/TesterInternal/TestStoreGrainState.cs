using System;
using System.Globalization;
using Orleans;
using Orleans.Internal;

namespace UnitTests.Persistence
{
    [Serializable]
    public class TestStoreGrainState
    {
        public string A { get; set; }
        public int B { get; set; }
        public long C { get; set; }

        internal static GrainState<TestStoreGrainState> NewRandomState(int? aPropertyLength = null)
        {
            return new GrainState<TestStoreGrainState>
            {
                State = new TestStoreGrainState
                {
                    A = aPropertyLength == null
                        ? ThreadSafeRandom.Next().ToString(CultureInfo.InvariantCulture)
                        : GenerateRandomDigitString(aPropertyLength.Value),
                    B = ThreadSafeRandom.Next(),
                    C = ThreadSafeRandom.Next()
                }
            };
        }

        private static string GenerateRandomDigitString(int stringLength)
        {
            var characters = new char[stringLength];
            for (var i = 0; i < stringLength; ++i)
            {
                characters[i] = (char)ThreadSafeRandom.Next('0', '9' + 1);
            }
            return new string(characters);
        }
    }
}


