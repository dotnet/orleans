namespace UnitTests.GrainInterfaces
{
    public interface IMultifacetFactoryTestGrain : IGrainWithIntegerKey
    {
        Task<IMultifacetReader> GetReader(IMultifacetTestGrain grain);
        Task<IMultifacetReader> GetReader();
        Task<IMultifacetWriter> GetWriter(IMultifacetTestGrain grain);
        Task<IMultifacetWriter> GetWriter();
        Task SetReader(IMultifacetReader reader);
        Task SetWriter(IMultifacetWriter writer);
    }
}