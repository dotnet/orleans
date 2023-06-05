namespace UnitTests.Grains
{
    [GenerateSerializer]
    public enum IntEnum
    {
        Value1,
        Value2,
        Value3
    }

    [GenerateSerializer]
    public enum UShortEnum : ushort
    {
        Value1,
        Value2,
        Value3
    }

    [GenerateSerializer]
    public enum CampaignEnemyType : sbyte
    {
        None = -1,
        Brute = 0,
        Enemy1,
        Enemy2,
        Enemy3,
        Enemy4,
    }

    public class UnserializableException : Exception
    {
        public UnserializableException(string message) : base(message)
        { }
    }

    [Serializable]
    [GenerateSerializer]
    public class Unrecognized
    {
        [Id(0)]
        public int A { get; set; }
        [Id(1)]
        public int B { get; set; }
    }

    [Serializable]
    [GenerateSerializer]
    public class ClassWithCustomSerializer
    {
        [Id(0)]
        public int IntProperty { get; set; }
        [Id(1)]
        public string StringProperty { get; set; }

        public static int SerializeCounter { get; set; }
        public static int DeserializeCounter { get; set; }

        static ClassWithCustomSerializer()
        {
            SerializeCounter = 0;
            DeserializeCounter = 0;
        }
    }
}
