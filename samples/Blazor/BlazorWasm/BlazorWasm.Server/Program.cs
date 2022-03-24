using Microsoft.OpenApi.Models;
using Orleans;
using Orleans.Hosting;
using BlazorWasm.Grains;
using Sample.Silo.Api;

await Host.CreateDefaultBuilder(args)
    .UseOrleans(builder =>
    {
        builder.ConfigureApplicationParts(manager =>
        {
            manager.AddApplicationPart(typeof(WeatherGrain).Assembly).WithReferences();
        });
        builder.UseLocalhostClustering();
        builder.AddMemoryGrainStorageAsDefault();
        builder.AddSimpleMessageStreamProvider("SMS");
        builder.AddMemoryGrainStorage("PubSubStore");
    })
    .ConfigureWebHostDefaults(webBuilder =>
    {
        webBuilder
            .ConfigureServices(services =>
            {
                services.AddControllers()
                    .AddApplicationPart(typeof(WeatherController).Assembly);

                services.AddSwaggerGen(options =>
                {
                    options.SwaggerDoc("v1", new OpenApiInfo { Title = nameof(Sample), Version = "v1" });
                });

                services.AddCors(options =>
                {
                    options.AddPolicy("ApiService",
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
                app.UseCors("ApiService");
                app.UseSwagger();
                app.UseSwaggerUI(options =>
                {
                    options.SwaggerEndpoint("/swagger/v1/swagger.json", nameof(Sample));
                });

                app.UseRouting();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapDefaultControllerRoute();
                });
            })
            .UseUrls("http://localhost:5000");
    })
    .ConfigureServices(services =>
    {
        services.Configure<ConsoleLifetimeOptions>(options =>
        {
            options.SuppressStatusMessages = true;
        });
    })
    .RunConsoleAsync();