using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using System.IO;

namespace GeneratorTestGrain
{
    public class GeneratorTestDerivedGrain2 : GeneratorTestGrain, IGeneratorTestDerivedGrain2
    {
        public Task<string> StringConcat(string str1, string str2, string str3)
        {
            return Task.FromResult((String.Concat(str1, str2, str3)));
        }
    }
}