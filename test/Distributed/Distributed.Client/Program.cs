using System.CommandLine;
using System.CommandLine.Parsing;
using Distributed.Client;
using Distributed.Client.Scenarios;

var root = new RootCommand();

root.Add(Scenario.CreateCommand(new PingScenario()));

await root.InvokeAsync(args);
