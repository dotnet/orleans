using HelloWorld.Interfaces;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace HelloWorld.Grains
{
    /// <summary>
    /// Orleans grain implementation class HelloGrain.
    /// </summary>
    public class HelloGrain : Orleans.Grain, IHelloGrain
    {
        private readonly ILogger _logger;

        public HelloGrain(ILogger<HelloGrain> logger)
        {
            _logger = logger;
        }  

        Task<string> IHelloGrain.SayHello(string greeting)
        {
            _logger.LogInformation($"SayHello message received: greeting = '{greeting}'");
            return Task.FromResult($"You said: '{greeting}', I say: Hello!");
        }
    }
}
