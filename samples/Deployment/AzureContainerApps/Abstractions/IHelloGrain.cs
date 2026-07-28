using Orleans;

namespace Abstractions
{
    public interface IHelloGrain : IGrainWithStringKey
    {
        Task<string> SayHello();
    }
}