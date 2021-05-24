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
    public static class OrleansExtension 
    {
        public static ISiloBuilder UseShutdownTimeout(this ISiloBuilder silo, TimeSpan shutdown)
        {
            // BTW: there is a bug on DOTNET_SHUTDOWNTIMEOUTSECONDS
            // please refer to https://github.com/dotnet/runtime/issues/36059
            // configure DeactivationTimeout as well due to the issue here
            // https://github.com/dotnet/orleans/issues/6832
            silo.Configure<HostOptions>(x => x.ShutdownTimeout = shutdown)
                .Configure<GrainCollectionOptions>(x => x.DeactivationTimeout = shutdown);
            return silo;
        }    
    }

    public class Program
    {
        public static Task Main(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .UseOrleans(siloBuilder =>
                {
                    siloBuilder
                    .UseLocalhostClustering()
                    .UseShutdownTimeout(TimeSpan.FromMinutes(1))
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
