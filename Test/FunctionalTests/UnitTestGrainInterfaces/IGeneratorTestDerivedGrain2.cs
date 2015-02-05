using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using System.IO;

namespace GeneratorTestGrain
{
    public interface IGeneratorTestDerivedGrain2 : IGeneratorTestGrain
    {
        Task<string> StringConcat(string str1, string str2, string str3);
    }
}