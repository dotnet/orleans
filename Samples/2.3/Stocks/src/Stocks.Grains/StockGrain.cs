using System;
using System.Net.Http;
using System.Threading.Tasks;
using Orleans;
using Stocks.Interfaces;

namespace Stocks.Grains
{
    public class StockGrain : Grain, IStockGrain
    {
        // request api key from here https://www.alphavantage.co/support/#api-key
        private const string ApiKey = "demo";
        string price;
        string graphData;

        public override async Task OnActivateAsync()
        {
            this.GetPrimaryKey(out var stock);
            await UpdatePrice(stock);

            RegisterTimer(
                UpdatePrice,
                stock,
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(1));

            await base.OnActivateAsync();
        }

        async Task UpdatePrice(object stock)
        {
            // collect the task variables without awaiting
            var priceTask = GetPriceQuote(stock as string);
            var graphDataTask = GetDailySeries(stock as string);

            // await both tasks
            await Task.WhenAll(priceTask, graphDataTask);

            // read the results
            price = priceTask.Result;
            graphData = graphDataTask.Result;
            Console.WriteLine(price);
        }

        async Task<string> GetPriceQuote(string stock)
        {
            var uri = $"https://www.alphavantage.co/query?function=GLOBAL_QUOTE&symbol={stock}&apikey={ApiKey}&datatype=csv";
            using (var http = new HttpClient())
            using (var resp = await http.GetAsync(uri))
            {
                return await resp.Content.ReadAsStringAsync();
            }
        }

        async Task<string> GetDailySeries(string stock)
        {
            var uri = $"https://www.alphavantage.co/query?function=TIME_SERIES_DAILY&symbol={stock}&apikey={ApiKey}&datatype=csv";
            using (var http = new HttpClient())
            using (var resp = await http.GetAsync(uri))
            {
                return await resp.Content.ReadAsStringAsync();
            }
        }

        public Task<string> GetPrice()
        {
            return Task.FromResult(price);
        }
    }
}