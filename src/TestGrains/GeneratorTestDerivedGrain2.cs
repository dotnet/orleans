using System;
using System.Threading.Tasks;
using TestGrainInterfaces;

namespace TestGrains
{
    public class GeneratorTestDerivedGrain2 : TestGrains.GeneratorTestGrain, IGeneratorTestDerivedGrain2
    {
        public Task<string> StringConcat(string str1, string str2, string str3)
        {
            return Task.FromResult((String.Concat(str1, str2, str3)));
        }
    }
}