using Microsoft.Extensions.Hosting;
using MongoDBDocumentation;

using var host = MongoDBConfiguration.ConfigureAspireSilo(args);
await host.RunAsync();
