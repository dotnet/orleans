using System.CommandLine;
using System.CommandLine.Parsing;
using DistributedTests.Client.Commands;
using DistributedTests.Client.LoadGeneratorScenario;
using Microsoft.Extensions.Logging;

var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information)
                                                           .AddSimpleConsole(options => options.SingleLine = true));

var root = new RootCommand
{
    Scenario.CreateCommand(new PingScenario(), loggerFactory),
    Scenario.CreateCommand(new FanOutScenario(), loggerFactory),
    new CounterCaptureCommand(loggerFactory.CreateLogger<CounterCaptureCommand>()),
    new ChaosAgentCommand(loggerFactory.CreateLogger<ChaosAgentCommand>())
};

await root.InvokeAsync(args);
