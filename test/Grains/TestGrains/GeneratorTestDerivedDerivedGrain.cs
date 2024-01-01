using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class GeneratorTestDerivedDerivedGrain : GeneratorTestDerivedGrain2, IGeneratorTestDerivedDerivedGrain
    {
        public Task<string> StringNConcat(string[] strArray)
        {
            string strAll = string.Empty;
            foreach(string str in strArray)
                strAll = string.Concat(strAll, str);

            return Task.FromResult(strAll);
        }

        public Task<string> StringReplace(ReplaceArguments strs)
        {
            myGrainString = myGrainString.Replace(strs.OldString, strs.NewString);
            return Task.FromResult(myGrainString);
        }
    }
}