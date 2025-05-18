using System.Diagnostics.CodeAnalysis;
using Orleans;
using Orleans.Hosting;

[assembly: Experimental("ORLEANSEXP005")]
[assembly: RegisterProvider("Cosmos", "GrainJournaling", "Silo", typeof(CosmosGrainJournalingProviderBuilder))]
