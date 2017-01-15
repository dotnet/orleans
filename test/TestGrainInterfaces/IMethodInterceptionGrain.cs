using System;
using System.Threading.Tasks;

namespace UnitTests.GrainInterfaces
{
    using Orleans;
    public interface IMethodInterceptionGrain : IGrainWithIntegerKey, IMethodFromAnotherInterface
    {
        Task<string> One();
        Task<string> Echo(string someArg);
        Task<string> NotIntercepted();
    }

    public interface IGenericMethodInterceptionGrain<in T> : IGrainWithIntegerKey, IMethodFromAnotherInterface
    {
        Task<string> GetInputAsString(T input);
    }

    public interface IMethodFromAnotherInterface
    {
        Task<string> SayHello();
    }
    
    public interface ITrickyMethodInterceptionGrain : IGenericMethodInterceptionGrain<string>, IGenericMethodInterceptionGrain<bool>
    {
        Task<int> GetBestNumber();
    }
}
