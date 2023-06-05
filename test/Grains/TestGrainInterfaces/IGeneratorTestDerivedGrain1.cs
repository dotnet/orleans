namespace UnitTests.GrainInterfaces
{
    public interface IGeneratorTestDerivedGrain1 : IGeneratorTestGrain
    {
        Task<byte[]> ByteAppend(byte[] data);
    }
}