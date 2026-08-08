using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Orleans.Hosting;

namespace GoogleFirestore;

public static class GoogleFirestoreConfiguration
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

            siloBuilder.UseGoogleFirestoreClustering(options =>
            {
                options.ProjectId = projectId;
                options.RootCollectionName = rootCollectionName;
                options.EmulatorHost = emulatorHost;
            });

            siloBuilder.UseGoogleFirestoreGrainDirectoryAsDefault(options =>
            {
                options.ProjectId = projectId;
                options.RootCollectionName = rootCollectionName;
                options.EmulatorHost = emulatorHost;
            });

            siloBuilder.AddGoogleFirestoreGrainStorage("profiles", options =>
            {
                options.ProjectId = projectId;
                options.RootCollectionName = rootCollectionName;
                options.EmulatorHost = emulatorHost;
            });

            siloBuilder.UseGoogleFirestoreReminderService(options =>
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

            clientBuilder.UseGoogleFirestoreClustering(options =>
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
        siloBuilder.AddGoogleFirestoreGrainStorage(
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
