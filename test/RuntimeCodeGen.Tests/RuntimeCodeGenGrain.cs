using System;
using System.Threading.Tasks;
using Orleans;

namespace RuntimeCodeGen.Interfaces
{
    public interface IRuntimeCodeGenGrain : IGrainWithGuidKey
    {
        Task<RuntimeCodeGenPoco> SomeMethod(RuntimeCodeGenPoco poco);
    }

    public class RuntimeCodeGenGrain : Grain, IRuntimeCodeGenGrain
    {
        public Task<RuntimeCodeGenPoco> SomeMethod(RuntimeCodeGenPoco poco) => Task.FromResult(poco);
    }

    [Serializable]
    public class RuntimeCodeGenPoco
    {
    }
}
