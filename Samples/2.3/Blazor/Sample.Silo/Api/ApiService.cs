using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Swashbuckle.AspNetCore.Swagger;
using System.Threading;
using System.Threading.Tasks;

namespace Sample.Silo.Api
{
    public class ApiService : IHostedService
    {
        private readonly IWebHost host;

        public ApiService(IGrainFactory factory, ILoggerProvider loggerProvider)
        {
            host = WebHost
                .CreateDefaultBuilder()
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.AddProvider(loggerProvider);
                })
                .ConfigureServices(services =>
                {
                    services.AddSingleton(factory);

                    services.AddMvc()
                        .SetCompatibilityVersion(CompatibilityVersion.Latest)
                        .AddApplicationPart(typeof(WeatherController).Assembly)
                        .AddControllersAsServices();

                    services.AddSwaggerGen(options =>
                    {
                        options.SwaggerDoc("v0", new Info
                        {
                            Title = nameof(Sample),
                            Version = "v0"
                        });
                    });

                    services.AddCors(options =>
                    {
                        options.AddPolicy(nameof(ApiService),
                            builder =>
                            {
                                builder
                                    .WithOrigins(
                                        "http://localhost:62653",
                                        "http://localhost:62654")
                                    .AllowAnyMethod()
                                    .AllowAnyHeader();
                            });
                    });
                })
                .Configure(app =>
                {
                    app.UseCors(nameof(ApiService));
                    app.UseSwagger();
                    app.UseSwaggerUI(options =>
                    {
                        options.SwaggerEndpoint("/swagger/v0/swagger.json", nameof(Sample));
                    });
                    app.UseMvc();
                })
                .UseUrls("http://localhost:8081")
                .Build();
        }

        public Task StartAsync(CancellationToken cancellationToken) =>
            host.StartAsync(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken) =>
            host.StopAsync(cancellationToken);
    }
}