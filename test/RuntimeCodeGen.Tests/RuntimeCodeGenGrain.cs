using System;
using System.Threading.Tasks;
using Orleans;

namespace RuntimeCodeGen.Interfaces
{
    public interface IRuntimeCodeGenGrain : IGrainWithGuidKey
    {
        Task<RuntimeCodeGenPoco> SomeMethod(RuntimeCodeGenPoco poco);

        ValueTask<RuntimeCodeGenPoco> ValueTaskMethod(RuntimeCodeGenPoco poco);
    }

    public class RuntimeCodeGenGrain : Grain, IRuntimeCodeGenGrain
    {
        public Task<RuntimeCodeGenPoco> SomeMethod(RuntimeCodeGenPoco poco) => Task.FromResult(poco);

        public ValueTask<RuntimeCodeGenPoco> ValueTaskMethod(RuntimeCodeGenPoco poco) =>
            new ValueTask<RuntimeCodeGenPoco>(poco);
    }

    [Serializable]
    public class RuntimeCodeGenPoco
    {
    }
}
