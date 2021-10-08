using System.CommandLine;
using System.CommandLine.Parsing;
using Distributed.Silo;
using Distributed.Silo.Configurator;

var root = new RootCommand();

root.Add(Silo.CreateCommand(new SimpleSilo()));
root.Add(Silo.CreateCommand(new EventGeneratorStreamingSilo()));

await root.InvokeAsync(args);