using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using BlazorWasm.Services;

namespace BlazorWasm
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
                options.BaseAddress = new Uri("http://localhost:5000/api");
            });

            await builder.Build().RunAsync();
        }
    }
}