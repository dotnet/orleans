using System.Globalization;

namespace UnitTests.Persistence
{
    [Serializable]
    [GenerateSerializer]
    public class TestStoreGrainState
    {
        [Id(0)]
        public string A { get; set; }
        [Id(1)]
        public int B { get; set; }
        [Id(2)]
        public long C { get; set; }

        internal static GrainState<TestStoreGrainState> NewRandomState(int? aPropertyLength = null)
        {
            return new GrainState<TestStoreGrainState>
            {
                State = new TestStoreGrainState
                {
                    A = aPropertyLength == null
                        ? Random.Shared.Next().ToString(CultureInfo.InvariantCulture)
                        : GenerateRandomDigitString(aPropertyLength.Value),
                    B = Random.Shared.Next(),
                    C = Random.Shared.Next()
                }
            };
        }

        private static string GenerateRandomDigitString(int stringLength)
        {
            var characters = new char[stringLength];
            for (var i = 0; i < stringLength; ++i)
            {
                characters[i] = (char)Random.Shared.Next('0', '9' + 1);
            }
            return new string(characters);
        }
    }
}


