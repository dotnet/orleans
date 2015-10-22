using System.Threading.Tasks;

namespace TestGrainInterfaces
{
    public interface IGeneratorTestDerivedGrain1 : IGeneratorTestGrain
    {
        Task<byte[]> ByteAppend(byte[] data);
    }
}