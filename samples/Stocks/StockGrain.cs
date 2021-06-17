using System;
using System.Net.Http;
using System.Threading.Tasks;
using Orleans;
using Stocks.Interfaces;

namespace Stocks.Grains
{
    public class StockGrain : Grain, IStockGrain
    {
        // Request api key from here https://www.alphavantage.co/support/#api-key
        private const string ApiKey = "5NVLFTOEC34MVTDE";
        private readonly HttpClient _httpClient = new();

        private string _price;

        public override async Task OnActivateAsync()
        {
            this.GetPrimaryKey(out var stock);
            await UpdatePrice(stock);

            RegisterTimer(
                UpdatePrice,
                stock,
                TimeSpan.FromMinutes(2),
                TimeSpan.FromMinutes(2));

            await base.OnActivateAsync();
        }

        private async Task UpdatePrice(object stock)
        {
            var priceTask = GetPriceQuote((string)stock);

            // read the results
            _price = await priceTask;
        }

        private async Task<string> GetPriceQuote(string stock)
        {
            using var resp = await _httpClient.GetAsync($"https://www.alphavantage.co/query?function=GLOBAL_QUOTE&symbol={stock}&apikey={ApiKey}&datatype=csv");
            return await resp.Content.ReadAsStringAsync();
        }

        public Task<string> GetPrice() => Task.FromResult(_price);
    }
}