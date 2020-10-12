using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Sample.ClientSide.Services;

namespace Sample.ClientSide
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var builder = WebAssemblyHostBuilder.CreateDefault(args);
            builder.RootComponents.Add<App>("app");

            builder.Services.AddSingleton<HttpClient>();
            builder.Services.AddApiService(options =>
            {
                options.BaseAddress = new Uri("http://localhost:8081/api");
            });

            await builder.Build().RunAsync();
        }
    }
}