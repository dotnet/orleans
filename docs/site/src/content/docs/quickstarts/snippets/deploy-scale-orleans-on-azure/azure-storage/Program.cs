// <azure-storage-usings>
using Azure.Data.Tables;
using Azure.Identity;
using Orleans.Configuration;
// </azure-storage-usings>

var builder = WebApplication.CreateBuilder(args);

// <azure-storage-configuration>
if (builder.Environment.IsDevelopment())
{
    builder.Host.UseOrleans(static siloBuilder =>
    {
        siloBuilder
            .UseLocalhostClustering()
            .AddMemoryGrainStorage("urls");
    });
}
else
{
    builder.Host.UseOrleans(siloBuilder =>
    {
        var endpoint = new Uri(builder.Configuration["AZURE_TABLE_STORAGE_ENDPOINT"]!);
        var credential = new DefaultAzureCredential();

        siloBuilder
            .UseAzureStorageClustering(options =>
            {
                options.TableServiceClient = new TableServiceClient(endpoint, credential);
            })
            .AddAzureTableGrainStorage(name: "urls", options =>
            {
                options.TableServiceClient = new TableServiceClient(endpoint, credential);
            })
            .Configure<ClusterOptions>(options =>
            {
                options.ClusterId = "url-shortener";
                options.ServiceId = "urls";
            });
    });
}
// </azure-storage-configuration>

var app = builder.Build();
app.Run();
