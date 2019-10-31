using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Hosting;
using System.Security.Cryptography.X509Certificates;
using System.Net;
using Orleans.Configuration;

namespace OrleansSiloHost
{
    public class Program
    {
        public static async Task<int> Main(string[] args)
        {
            try
            {
                var host = new HostBuilder()
                    .UseEnvironment(Environments.Development)
                    .UseOrleans((context, siloBuilder) =>
                    {
                        var isDevelopment = context.HostingEnvironment.IsDevelopment();
                        siloBuilder
                            .UseLocalhostClustering(serviceId: "HelloWorldApp", clusterId: "dev")
                            .Configure<ConnectionOptions>(options =>
                            {
                                options.ProtocolVersion = Orleans.Runtime.Messaging.NetworkProtocolVersion.Version2;
                            })
                            .UseTls(StoreName.My, "fakedomain.faketld", allowInvalid: isDevelopment, StoreLocation.LocalMachine, options =>
                            {
                                // In this sample there is only one silo, however if there are multiple silos then the TargetHost must be set
                                // for each connection which is initiated.
                                options.OnAuthenticateAsClient = (connection, sslOptions) =>
                                {
                                    sslOptions.TargetHost = "fakedomain.faketld";
                                };

                                if (isDevelopment)
                                {
                                    // NOTE: Do not do this in a production environment
                                    options.AllowAnyRemoteCertificate();
                                }
                            });
                    })
                    .ConfigureLogging(logging => logging.AddConsole())
                    .Build();
                await host.RunAsync();

                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return 1;
            }
        }
    }
}
