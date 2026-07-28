using HelloWorld.Interfaces;
using Microsoft.Extensions.Logging;

namespace HelloWorld.Grains;

// <hello_grain>
public class HelloGrain : Orleans.Grain, IHello
{
    private readonly ILogger<HelloGrain> _logger;

    public HelloGrain(ILogger<HelloGrain> logger) => _logger = logger;

    Task<string> IHello.SayHello(string greeting)
    {
        _logger.LogInformation("SayHello message received: greeting = '{Greeting}'", greeting);
        return Task.FromResult($"You said: '{greeting}', I say: Hello!");
    }
}
// </hello_grain>
