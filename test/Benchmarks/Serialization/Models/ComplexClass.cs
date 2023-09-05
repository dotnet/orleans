namespace Benchmarks.Models
{
    [Serializable]
    [GenerateSerializer]
    public class ComplexClass : SimpleClass
    {
        [Id(0)]
        public int Int { get; set; }

        [Id(1)]
        public string String { get; set; }

        [Id(2)]
        public ComplexClass Self { get; set; }

        [Id(3)]
        public object AlsoSelf { get; set; }

        [Id(4)]
        public SimpleClass BaseSelf { get; set; }

        [Id(5)]
        public int[] Array { get; set; }

        [Id(6)]
        public int[,] MultiDimensionalArray { get; set; }
    }
}