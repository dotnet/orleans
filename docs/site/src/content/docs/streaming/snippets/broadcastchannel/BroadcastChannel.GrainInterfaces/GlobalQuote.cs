using System.Text.Json.Serialization;

namespace BroadcastChannel.GrainInterfaces;

[GenerateSerializer]
public sealed class GlobalQuote
{
    [
        Id(0),
        JsonConverter(typeof(JsonStringEnumConverter)),
        JsonPropertyName("01. symbol")
    ]
    public StockSymbol Symbol { get; set; }

    [Id(1), JsonPropertyName("02. open")]
    public decimal Open { get; set; }

    [Id(2), JsonPropertyName("03. high")]
    public decimal High { get; set; }

    [Id(3), JsonPropertyName("04. low")]
    public decimal Low { get; set; }

    [Id(4), JsonPropertyName("05. price")]
    public decimal Price { get; set; }

    [Id(5), JsonPropertyName("06. volume")]
    public long Volume { get; set; }

    [Id(6), JsonPropertyName("07. latest trading day")]
    public DateOnly LatestTradingDay { get; set; }

    [Id(7), JsonPropertyName("08. previous close")]
    public decimal PreviousClose { get; set; }

    [Id(8), JsonPropertyName("09. change")]
    public decimal Change { get; set; }

    [Id(9), JsonPropertyName("10. change percent")]
    public string ChangePercent { get; set; } = null!;
}