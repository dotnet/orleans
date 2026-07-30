using GrainStorage;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.UseOrleans(siloBuilder =>
{
    siloBuilder.UseLocalhostClustering()
        .AddFileGrainStorage("File", options =>
        {
            string path = Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData);

            options.RootDirectory = Path.Combine(path, "Orleans/GrainState/v1");
        });
});

using var host = builder.Build();
await host.RunAsync();
