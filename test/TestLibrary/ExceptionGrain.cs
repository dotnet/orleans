using System;

using System.Threading.Tasks;
using Orleans;

namespace TestLibrary
{
    public interface IExceptionGrain : IGrainWithGuidKey
    {
        Task Throw();
    }
    
    public class ExceptionGrain : Grain, IExceptionGrain
    {
        public Task Throw() => throw new InvalidOperationException("Throwing");
    }
}