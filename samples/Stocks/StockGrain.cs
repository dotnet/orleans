using Stocks.Interfaces;

namespace Stocks.Grains;

public sealed class StockGrain : Grain, IStockGrain
{
    // Request api key from here https://www.alphavantage.co/support/#api-key
    private const string ApiKey = "5NVLFTOEC34MVTDE";
    private readonly HttpClient _httpClient = new();

    private string _price = null!;

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var stock = this.GetPrimaryKeyString();
        await UpdatePrice(stock, cancellationToken);

        this.RegisterGrainTimer(
            UpdatePrice,
            stock,
            new GrainTimerCreationOptions
            {
                DueTime = TimeSpan.FromMinutes(2),
                Period = TimeSpan.FromMinutes(2),
                Interleave = true
            });

        await base.OnActivateAsync(cancellationToken);
    }

    private async Task UpdatePrice(string stock, CancellationToken cancellationToken)
    {
        _price = await GetPriceQuote(stock, cancellationToken);
    }

    private async Task<string> GetPriceQuote(string stock, CancellationToken cancellationToken)
    {
        using var resp =
            await _httpClient.GetAsync(
                $"https://www.alphavantage.co/query?function=GLOBAL_QUOTE&symbol={stock}&apikey={ApiKey}&datatype=csv",
                cancellationToken);

        return await resp.Content.ReadAsStringAsync(cancellationToken);
    }

    public Task<string> GetPrice() => Task.FromResult(_price);
}
