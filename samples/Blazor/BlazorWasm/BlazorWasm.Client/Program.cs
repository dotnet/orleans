using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using BlazorWasm;
using BlazorWasm.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("app");

builder.Services.AddSingleton<HttpClient>();
builder.Services.AddApiService(
    options => options.BaseAddress = new Uri("http://localhost:5000/api"));

await builder.Build().RunAsync();
