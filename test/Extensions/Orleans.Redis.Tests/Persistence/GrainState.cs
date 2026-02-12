using UnitTests.GrainInterfaces;

namespace Tester.Redis.Persistence
{
    [GenerateSerializer]
    public class GrainState
    {
        [Id(0)]
        public string StringValue { get; set; }
        [Id(1)]
        public int IntValue { get; set; }
        [Id(2)]
        public DateTime DateTimeValue { get; set; }
        [Id(3)]
        public Guid GuidValue { get; set; }
        [Id(4)]
        public IGrainStorageGenericGrain<GrainState> GrainValue { get; set; }
    }
}