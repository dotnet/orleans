namespace UnitTests.GrainInterfaces
{
    [Serializable]
    [Orleans.GenerateSerializer]
    public class TestTypeA
    {
        [Orleans.Id(0)]
        public ICollection<TestTypeA> Collection { get; set; }
    }
}
