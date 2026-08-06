using Orleans;

namespace Abstractions
{
    public interface IHelloGrain : IGrainWithIntegerKey
    {
        Task<string> SayHello();
    }
}