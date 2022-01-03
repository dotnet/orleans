using System.CommandLine;
using System.CommandLine.Parsing;
using DistributedTests.Server;
using DistributedTests.Server.Configurator;

var root = new RootCommand();

root.Add(Server.CreateCommand(new SimpleSilo()));
root.Add(Server.CreateCommand(new EventGeneratorStreamingSilo()));

await root.InvokeAsync(args);