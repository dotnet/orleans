using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Orleans.Hosting;

namespace FirestoreDocumentation;

public static class FirestoreConfiguration
{
    public static void ConfigureSilo(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        var projectId = builder.Configuration["GoogleCloud:ProjectId"]
            ?? throw new InvalidOperationException("GoogleCloud:ProjectId is required.");
        var emulatorHost = builder.Configuration["GoogleCloud:EmulatorHost"];
        var rootCollectionName = builder.Configuration["GoogleCloud:RootCollectionName"] ?? "Orleans";

        // <google_firestore_silo>
        builder.UseOrleans(siloBuilder =>
        {
            siloBuilder.Configure<ClusterOptions>(options =>
            {
                options.ClusterId = "orders-production";
                options.ServiceId = "orders";
            });

            siloBuilder.UseFirestoreClustering(options =>
            {
                options.ProjectId = projectId;
                options.RootCollectionName = rootCollectionName;
                options.EmulatorHost = emulatorHost;
            });

            siloBuilder.UseFirestoreGrainDirectoryAsDefault(options =>
            {
                options.ProjectId = projectId;
                options.RootCollectionName = rootCollectionName;
                options.EmulatorHost = emulatorHost;
            });

            siloBuilder.AddFirestoreGrainStorage("profiles", options =>
            {
                options.ProjectId = projectId;
                options.RootCollectionName = rootCollectionName;
                options.EmulatorHost = emulatorHost;
            });

            siloBuilder.UseFirestoreReminderService(options =>
            {
                options.ProjectId = projectId;
                options.RootCollectionName = rootCollectionName;
                options.EmulatorHost = emulatorHost;
            });
        });
        // </google_firestore_silo>
    }

    public static void ConfigureClient(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        var projectId = builder.Configuration["GoogleCloud:ProjectId"]
            ?? throw new InvalidOperationException("GoogleCloud:ProjectId is required.");
        var emulatorHost = builder.Configuration["GoogleCloud:EmulatorHost"];
        var rootCollectionName = builder.Configuration["GoogleCloud:RootCollectionName"] ?? "Orleans";

        // <google_firestore_client>
        builder.UseOrleansClient(clientBuilder =>
        {
            clientBuilder.Configure<ClusterOptions>(options =>
            {
                options.ClusterId = "orders-production";
                options.ServiceId = "orders";
            });

            clientBuilder.UseFirestoreClustering(options =>
            {
                options.ProjectId = projectId;
                options.RootCollectionName = rootCollectionName;
                options.EmulatorHost = emulatorHost;
            });
        });
        // </google_firestore_client>
    }

    public static void ConfigurePersistence(
        ISiloBuilder siloBuilder,
        string projectId,
        string? emulatorHost)
    {
        // <google_firestore_persistence>
        siloBuilder.AddFirestoreGrainStorage(
            "profiles",
            options =>
            {
                options.ProjectId = projectId;
                options.RootCollectionName = "Orleans";
                options.EmulatorHost = emulatorHost;
                options.DeleteStateOnClear = true;
            });
        // </google_firestore_persistence>
    }
}
