using System;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Hosting;
using Orleans.Configuration;
using System.Threading.Tasks;

namespace AspNetCoreCohosting
{
    public class Program
    {
        public static Task Main(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .UseOrleans(siloBuilder =>
                {
                    siloBuilder
                    .UseLocalhostClustering()
                    .Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromMinutes(1))
                    .Configure<ClusterOptions>(opts =>
                    {
                        opts.ClusterId = "dev";
                        opts.ServiceId = "HellowWorldAPIService";
                    })
                    .Configure<EndpointOptions>(opts =>
                    {
                        opts.AdvertisedIPAddress = IPAddress.Loopback;
                    });
                })
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.Configure((ctx, app) =>
                    {
                        if (ctx.HostingEnvironment.IsDevelopment())
                        {
                            app.UseDeveloperExceptionPage();
                        }

                        app.UseHttpsRedirection();
                        app.UseRouting();
                        app.UseAuthorization();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapControllers();
                        });
                    });
                })
                .ConfigureServices(services =>
                {
                    services.AddControllers();
                })
            .RunConsoleAsync();
    }
}
