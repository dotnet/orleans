using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers.MongoDB.Configuration;
using Orleans.Providers.MongoDB.StorageProviders.Serializers;

namespace MongoDBDocumentation;

public static class MongoDBConfiguration
{
    public static void ConfigureSilo(string connectionString)
    {
        var builder = Host.CreateApplicationBuilder();
        // <mongodb_silo>
        builder.UseOrleans(silo =>
        {
            silo.Configure<ClusterOptions>(options =>
            {
                options.ClusterId = "orders-production";
                options.ServiceId = "orders";
            });
            silo.UseMongoDBClient(connectionString);
            silo.UseMongoDBClustering(options =>
            {
                options.DatabaseName = "orders";
                options.Strategy = MongoDBMembershipStrategy.Multiple;
            });
            silo.AddMongoDBGrainStorage("profiles", options =>
            {
                options.DatabaseName = "orders";
                options.GrainStateSerializer = new BsonGrainStateSerializer();
            });
            silo.UseMongoDBReminders(options => options.DatabaseName = "orders");
        });
        // </mongodb_silo>
    }

    public static void ConfigureClient(string connectionString)
    {
        var builder = Host.CreateApplicationBuilder();
        // <mongodb_client>
        builder.UseOrleansClient(client =>
        {
            client.Configure<ClusterOptions>(options =>
            {
                options.ClusterId = "orders-production";
                options.ServiceId = "orders";
            });
            client.UseMongoDBClient(connectionString);
            client.UseMongoDBClustering(options =>
            {
                options.DatabaseName = "orders";
                options.Strategy = MongoDBMembershipStrategy.Multiple;
            });
        });
        // </mongodb_client>
    }

    public static void ConfigureSerializers(ISiloBuilder silo)
    {
        // <mongodb_serializers>
        silo.Services.AddKeyedSingleton<IGrainStateSerializer, BinaryGrainStateSerializer>("PubSubStore");
        silo.AddMongoDBGrainStorage("PubSubStore", options => options.DatabaseName = "orders");
        // </mongodb_serializers>
    }

    public static void ConfigureFromConfiguration(string connectionString)
    {
        // <mongodb_configuration>
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddMongoDBClient(connectionString);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Orleans:ClusterId"] = "orders-production",
            ["Orleans:ServiceId"] = "orders",
            ["Orleans:Clustering:ProviderType"] = "MongoDB",
            ["Orleans:Clustering:DatabaseName"] = "orders",
            ["Orleans:Clustering:Strategy"] = "Multiple",
            ["Orleans:GrainStorage:profiles:ProviderType"] = "MongoDB",
            ["Orleans:GrainStorage:profiles:DatabaseName"] = "orders",
            ["Orleans:Reminders:ProviderType"] = "MongoDB",
            ["Orleans:Reminders:DatabaseName"] = "orders",
            ["Orleans:Reminders:CreateIndexes"] = "false"
        });
        builder.UseOrleans();
        // </mongodb_configuration>
    }
}
