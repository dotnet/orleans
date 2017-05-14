using System;
using System.Threading.Tasks;
using Orleans;

namespace OrleansInterface
{
    public interface IHelloGrain : IGrainWithIntegerKey
    {
        Task<string> SayHello(string greeting);
    }
}
