using System.Threading.Tasks;
using FasterSample.Core.Clocks;
using FasterSample.Core.Pipelines;
using FasterSample.Core.RandomGenerators;
using FasterSample.WebApp.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FasterSample.WebApp
{
    public static class Program
    {
        public static async Task Main(string[] args)
        {
            await Host
                .CreateDefaultBuilder(args)
                .ConfigureServices(services =>
                {
                    services
                        .AddOrleansClientService()
                        .AddRandomGenerator()
                        .AddSystemClock()
                        .AddAsyncPipelineFactory();
                })
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.ConfigureServices(services =>
                    {
                        services.AddRazorPages();
                        services.AddServerSideBlazor();
                        services.AddSingleton<WeatherForecastService>();
                    });

                    webBuilder.Configure((context, app) =>
                    {
                        var env = app.ApplicationServices.GetRequiredService<IWebHostEnvironment>();

                        if (env.IsDevelopment())
                        {
                            app.UseDeveloperExceptionPage();
                        }
                        else
                        {
                            app.UseExceptionHandler("/Error");
                            app.UseHsts();
                        }

                        app.UseHttpsRedirection();
                        app.UseStaticFiles();

                        app.UseRouting();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapBlazorHub();
                            endpoints.MapFallbackToPage("/_Host");
                        });
                    });
                })
                .RunConsoleAsync()
                .ConfigureAwait(false);
        }
    }
}