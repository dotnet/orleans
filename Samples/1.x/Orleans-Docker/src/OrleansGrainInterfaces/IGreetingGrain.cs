using System;
using System.Threading.Tasks;
using Orleans;

namespace OrleansGrainInterfaces
{
    public interface IGreetingGrain : IGrainWithGuidKey
    {
        Task<string> SayHello(string name);
    }
}
