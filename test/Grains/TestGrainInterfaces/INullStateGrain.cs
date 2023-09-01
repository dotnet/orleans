namespace UnitTests.GrainInterfaces
{
    [GenerateSerializer]
    public class NullableState
    {
        [Id(0)]
        public string Name { get; set; }
    }

    public interface INullStateGrain : IGrainWithIntegerKey
    {
        Task SetStateAndDeactivate(NullableState state);
        Task<NullableState> GetState();
    }
}