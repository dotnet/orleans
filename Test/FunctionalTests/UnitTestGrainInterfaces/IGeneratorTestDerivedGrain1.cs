using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using System.IO;

namespace GeneratorTestGrain
{
    public interface IGeneratorTestDerivedGrain1 : IGeneratorTestGrain
    {
        Task<byte[]> ByteAppend(byte[] data);
    }
}