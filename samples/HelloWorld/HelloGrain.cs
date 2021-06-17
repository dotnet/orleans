using System.Threading.Tasks;
using Orleans;

namespace HelloWorld
{
    public class HelloGrain : Grain, IHelloGrain
    {
        public Task<string> SayHello(string greeting) => Task.FromResult($"Hello, {greeting}!");
    }
}
