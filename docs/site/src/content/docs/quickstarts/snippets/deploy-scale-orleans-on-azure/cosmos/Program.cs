// <cosmos-usings>
using Azure.Identity;
using Orleans.Configuration;
// </cosmos-usings>

var builder = WebApplication.CreateBuilder(args);

// <cosmos-configuration>
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
        var endpoint = builder.Configuration["AZURE_COSMOS_DB_NOSQL_ENDPOINT"]!;
        var credential = new DefaultAzureCredential();

        siloBuilder
            .UseCosmosClustering(options =>
            {
                options.ConfigureCosmosClient(endpoint, credential);
            })
            .AddCosmosGrainStorage(name: "urls", options =>
            {
                options.ConfigureCosmosClient(endpoint, credential);
            })
            .Configure<ClusterOptions>(options =>
            {
                options.ClusterId = "url-shortener";
                options.ServiceId = "urls";
            });
    });
}
// </cosmos-configuration>

var app = builder.Build();
app.Run();
