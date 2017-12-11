using System.Runtime.CompilerServices;
using Orleans.CodeGeneration;
using Orleans.ServiceBus.Providers;

[assembly: InternalsVisibleTo("ServiceBus.Tests")]

[assembly: KnownAssembly(typeof(EventHubSequenceTokenV2))]
