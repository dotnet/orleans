using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class GeneratorTestDerivedGrain1 : GeneratorTestGrain, IGeneratorTestDerivedGrain1
    {
        public Task<byte[]> ByteAppend(byte[] data)
        {
            byte[] tmp = new byte[myGrainBytes.Length + data.Length];
            myGrainBytes.CopyTo(tmp, 0);
            data.CopyTo(tmp, myGrainBytes.Length);
            myGrainBytes = tmp;
            //RaiseStateUpdateEvent();
            return Task.FromResult(myGrainBytes);
        }
    }
}