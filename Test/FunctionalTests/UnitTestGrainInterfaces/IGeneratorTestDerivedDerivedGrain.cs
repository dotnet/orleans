using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using System.IO;

namespace GeneratorTestGrain
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