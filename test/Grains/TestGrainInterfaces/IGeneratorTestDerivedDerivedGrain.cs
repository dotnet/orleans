using System;
using System.Threading.Tasks;

namespace UnitTests.GrainInterfaces
{
    [Serializable]
    [Orleans.GenerateSerializer]
    public class ReplaceArguments
    {
        [Orleans.Id(0)]
        public string OldString { get; private set; }
        [Orleans.Id(1)]
        public string NewString { get; private set; }

        public ReplaceArguments(string oldStr, string newStr)
        {
            OldString = oldStr;
            NewString = newStr;
        }
    }

    public interface IGeneratorTestDerivedDerivedGrain : IGeneratorTestDerivedGrain2
    {
        Task<string> StringNConcat(string[] strArray);
        Task<string> StringReplace(ReplaceArguments strs);
    }
}