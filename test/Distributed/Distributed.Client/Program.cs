using System.CommandLine;
using System.CommandLine.Parsing;
using Distributed.Client;
using Distributed.Client.Commands;
using Distributed.Client.Scenarios;

var root = new RootCommand();

root.Add(Scenario.CreateCommand(new PingScenario()));
root.Add(new CounterCaptureCommand());
root.Add(new ChaosAgentCommand());

await root.InvokeAsync(args);
