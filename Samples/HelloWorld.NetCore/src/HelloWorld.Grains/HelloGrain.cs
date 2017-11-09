using HelloWorld.Interfaces;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace HelloWorld.Grains
{
    /// <summary>
    /// Orleans grain implementation class HelloGrain.
    /// </summary>
    public class HelloGrain : Orleans.Grain, IHello
    {
        private readonly ILogger logger;

        public HelloGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger("HelloWorld.Grains.HelloGrain");
        }  

        Task<string> IHello.SayHello(string greeting)
        {
            logger.LogWarning($"SayHello message received: greeting = '{greeting}'");
            return Task.FromResult($"You said: '{greeting}', I say: Hello!");
        }
    }
}
