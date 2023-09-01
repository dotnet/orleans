using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class GeneratorTestDerivedGrain2 : GeneratorTestGrain, IGeneratorTestDerivedGrain2
    {
        public Task<string> StringConcat(string str1, string str2, string str3)
        {
            return Task.FromResult((string.Concat(str1, str2, str3)));
        }
    }
}