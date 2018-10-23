using System;
using System.Net.Http;
using System.Threading.Tasks;
using HelloWorld.Interfaces;
using Orleans;

namespace HelloWorld.Grains
{
    public class StockGrain : Orleans.Grain, IStockGrain
    {
        private const string apiKey = "GYA8KUZ1L1MRDV7T";
        string price;
        string graphData;

        public override async Task OnActivateAsync()
        {
            string stock;
            this.GetPrimaryKey(out stock);
            await UpdatePrice(stock);

            RegisterTimer(
                UpdatePrice,
                stock,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5));

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
            var uri = $"https://www.alphavantage.co/query?function=GLOBAL_QUOTE&symbol={stock}&apikey={apiKey}&datatype=csv";
            using (var http = new HttpClient())
            using (var resp = await http.GetAsync(uri))
            {
                return await resp.Content.ReadAsStringAsync();
            }
        }

        async Task<string> GetDailySeries(string stock)
        {
            var uri = $"https://www.alphavantage.co/query?function=TIME_SERIES_DAILY&symbol={stock}&apikey={apiKey}&datatype=csv";
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