using System.Text.Json.Serialization;

namespace BroadcastChannel.GrainInterfaces;

[GenerateSerializer]
public sealed class Stock
{
    [Id(0), JsonPropertyName("Global Quote")]
    public GlobalQuote GlobalQuote { get; set; } = null!;
}