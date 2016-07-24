using System;
using System.Threading.Tasks;

namespace UnitTests.GrainInterfaces
{
    [Serializable]
    public class ReplaceArguments
    {
        public string OldString { get; private set; }
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