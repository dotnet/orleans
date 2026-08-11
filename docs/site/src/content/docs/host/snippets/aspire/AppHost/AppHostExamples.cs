using Aspire.Hosting;
using Aspire.Hosting.Azure;

namespace Orleans.Docs.Snippets.Aspire;

#pragma warning disable CS0219 // Variable is assigned but its value is never used

// This file contains examples for Orleans Aspire integration documentation.
// Each example is wrapped in a region marker and a method to allow compilation.

public static class AppHostExamples
{
    // <basic_orleans_cluster>
    public static void BasicOrleansCluster(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        // Add Redis for Orleans clustering
        var redis = builder.AddRedis("orleans-redis");

        // Define the Orleans resource with Redis clustering
        var orleans = builder.AddOrleans("cluster")
            .WithClustering(redis);

        // Add the Orleans silo project
        builder.AddProject<Projects.Silo>("silo")
            .WithReference(orleans)
            .WaitFor(redis)
            .WithReplicas(3);

        builder.Build().Run();
    }
    // </basic_orleans_cluster>

    // <orleans_with_storage_reminders>
    public static void OrleansWithStorageAndReminders(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        var redis = builder.AddRedis("orleans-redis");

        var orleans = builder.AddOrleans("cluster")
            .WithClustering(redis)
            .WithGrainStorage("Default", redis)
            .WithGrainStorage("PubSubStore", redis)
            .WithReminders(redis);

        builder.AddProject<Projects.Silo>("silo")
            .WithReference(orleans)
            .WaitFor(redis)
            .WithReplicas(3);

        builder.Build().Run();
    }
    // </orleans_with_storage_reminders>

    // <separate_silo_and_client>
    public static void SeparateSiloAndClient(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        var redis = builder.AddRedis("orleans-redis");

        var orleans = builder.AddOrleans("cluster")
            .WithClustering(redis)
            .WithGrainStorage("Default", redis);

        // Backend Orleans silo cluster
        var silo = builder.AddProject<Projects.Silo>("backend")
            .WithReference(orleans)
            .WaitFor(redis)
            .WithReplicas(5);

        // Frontend web project as Orleans client
        builder.AddProject<Projects.Client>("frontend")
            .WithReference(orleans.AsClient())  // Client-only reference
            .WaitFor(silo);

        builder.Build().Run();
    }
    // </separate_silo_and_client>

    // <azure_storage_aspire>
    public static void AzureStorageWithAspire(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        // Add Azure Storage for Orleans
        var storage = builder.AddAzureStorage("orleans-storage")
            .RunAsEmulator();  // Use Azurite emulator for local development

        var tables = storage.AddTables("orleans-tables");

        var orleans = builder.AddOrleans("cluster")
            .WithClustering(tables)
            .WithGrainStorage("Default", tables)
            .WithReminders(tables);

        builder.AddProject<Projects.Silo>("silo")
            .WithReference(orleans)
            .WaitFor(storage)
            .WithReplicas(3);

        builder.Build().Run();
    }
    // </azure_storage_aspire>

    // <local_development>
    public static void LocalDevelopment(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        var redis = builder.AddRedis("orleans-redis");
        // Redis container runs automatically during development

        var orleans = builder.AddOrleans("cluster")
            .WithClustering(redis);

        // ...
    }
    // </local_development>

    // <production_config>
    public static void ProductionConfig(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        // Use existing Azure Cache for Redis
        var redis = builder.AddConnectionString("orleans-redis");

        var orleans = builder.AddOrleans("cluster")
            .WithClustering(redis);

        // ...
    }
    // </production_config>

    // <reminders_redis_apphost>
    public static void RemindersRedisAppHost(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        var redis = builder.AddRedis("redis");

        var orleans = builder.AddOrleans("cluster")
            .WithClustering(redis)
            .WithReminders(redis);

        builder.AddProject<Projects.Silo>("silo")
            .WithReference(orleans)
            .WaitFor(redis);

        builder.Build().Run();
    }
    // </reminders_redis_apphost>

    // <reminders_azure_table_apphost>
    public static void RemindersAzureTableAppHost(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        var storage = builder.AddAzureStorage("storage")
            .RunAsEmulator();

        var reminders = storage.AddTables("reminders");

        var orleans = builder.AddOrleans("cluster")
            .WithClustering(reminders)
            .WithReminders(reminders);

        builder.AddProject<Projects.Silo>("silo")
            .WithReference(orleans)
            .WaitFor(storage);

        builder.Build().Run();
    }
    // </reminders_azure_table_apphost>

    // <reminders_inmemory_apphost>
    public static void RemindersInMemoryAppHost(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        var redis = builder.AddRedis("redis");

        var orleans = builder.AddOrleans("cluster")
            .WithClustering(redis)
            .WithMemoryReminders();

        builder.AddProject<Projects.Silo>("silo")
            .WithReference(orleans)
            .WaitFor(redis);

        builder.Build().Run();
    }
    // </reminders_inmemory_apphost>

    // <adonet_apphost>
    public static void AdoNetAppHost(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        // Add a SQL Server instance and database.
        // Note: Aspire infers the Orleans provider type from the resource class name
        // (SqlServerDatabaseResource → "SqlServerDatabase"), which does not match
        // the Orleans provider name "AdoNet".
        //
        // There is no public API to override this inference in the current version
        // of Aspire.Hosting.Orleans. As a workaround, configure the Orleans providers
        // manually in the silo using UseOrleans(siloBuilder => {...}) and read the
        // connection string from IConfiguration.
        var sql = builder.AddSqlServer("sql");
        var db = sql.AddDatabase("orleans-db");

        // Pass the database resource so Aspire injects ConnectionStrings__orleans-db.
        // Then configure Orleans manually in the silo (see silo example).
        builder.AddProject<Projects.Silo>("silo")
            .WithReference(db)
            .WaitFor(sql);

        builder.Build().Run();
    }
    // </adonet_apphost>

    // <grain_directory_apphost>
    public static void GrainDirectoryAppHost(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        var redis = builder.AddRedis("orleans-redis");

        var orleans = builder.AddOrleans("cluster")
            .WithClustering(redis)
            .WithGrainDirectory("MyDirectory", redis);

        builder.AddProject<Projects.Silo>("silo")
            .WithReference(orleans)
            .WaitFor(redis);

        builder.Build().Run();
    }
    // </grain_directory_apphost>

    // <explicit_cluster_ids>
    public static void ExplicitClusterIds(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        var redis = builder.AddRedis("orleans-redis");

        var orleans = builder.AddOrleans("cluster")
            // Set stable IDs for rolling deployments and cross-restart compatibility.
            // If omitted, random IDs are generated per run — fine for development,
            // but problematic in production because silos from different runs
            // will not recognize each other.
            .WithClusterId("my-cluster")
            .WithServiceId("my-service")
            .WithClustering(redis);

        builder.AddProject<Projects.Silo>("silo")
            .WithReference(orleans)
            .WaitFor(redis)
            .WithReplicas(3);

        builder.Build().Run();
    }
    // </explicit_cluster_ids>
}
