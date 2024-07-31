using System.CommandLine;
using System.CommandLine.Parsing;
using DistributedTests.Server;
using DistributedTests.Server.Configurator;

var root = new RootCommand
{
    Server.CreateCommand(new SimpleSilo()),
    Server.CreateCommand(new EventGeneratorStreamingSilo())
};

await root.InvokeAsync(args);