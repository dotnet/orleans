using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using System.IO;

namespace GeneratorTestGrain
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