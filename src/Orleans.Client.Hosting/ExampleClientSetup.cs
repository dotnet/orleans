using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;

namespace Orleans.Client.Hosting
{
    public class ExampleClientSetup
    {
        public ExampleClientSetup()
        {
            var services = new ServiceCollection();
            services.AddOrleansHostedClient(hostedClientBuilder =>
            {
                hostedClientBuilder
                    .AddOrleansClient("MainClientOne", clientBuilder =>
                    {
                        clientBuilder.UseLocalhostClustering(new int[1] { 30000 })
                        .Configure<ClusterOptions>(options =>
                        {
                            options.ClusterId = "dev";
                            options.ServiceId = "OrleansBasics";
                        })
                        .ConfigureLogging(logging => logging.AddConsole());
                    })
                    .AddOrleansClient("MainClientTwo", clientBuilder =>
                    {
                        clientBuilder.UseLocalhostClustering(new int[1] { 30001 })
                        .Configure<ClusterOptions>(options =>
                        {
                            options.ClusterId = "dev2";
                            options.ServiceId = "OrleansBasics2";
                        })
                        .ConfigureLogging(logging => logging.AddConsole());
                    });
            });
        }
    }
}
