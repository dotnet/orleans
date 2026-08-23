using Amazon;
using Amazon.CDK.AWS.DynamoDB;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Orleans;

namespace Orleans.Docs.Snippets.Aspire;

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

    // <azure_storage_providers_aspire>
    public static void AzureStorageProvidersWithAspire(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        var storage = builder.AddAzureStorage("orleans-storage")
            .RunAsEmulator();
        var tables = storage.AddTables("orleans-tables");
        var blobs = storage.AddBlobs("orleans-blobs");

        var orleans = builder.AddOrleans("cluster")
            .WithClustering(tables)
            .WithGrainStorage("table-state", tables)
            .WithGrainStorage("blob-state", blobs)
            .WithReminders(tables)
            .WithGrainDirectory("directory", tables);

        builder.AddProject<Projects.Silo>("silo")
            .WithReference(orleans)
            .WaitFor(storage);

        builder.Build().Run();
    }
    // </azure_storage_providers_aspire>

    // <cosmos_providers_aspire>
    public static void CosmosProvidersWithAspire(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        var cosmos = builder.AddAzureCosmosDB("orleans-cosmos")
            .RunAsEmulator();
        var database = cosmos.AddCosmosDatabase("orleans-db", "Orleans");
        database.AddContainer("membership", "/ClusterId", "OrleansCluster");
        database.AddContainer("state", "/PartitionKey", "OrleansStorage");
        database.AddContainer("reminders", "/PartitionKey", "OrleansReminders");

        var orleans = builder.AddOrleans("cluster")
            .WithClustering(cosmos)
            .WithGrainStorage("Default", cosmos)
            .WithReminders(cosmos);

        builder.AddProject<Projects.Silo>("silo")
            .WithReference(orleans)
            .WithEnvironment("Orleans__Clustering__DatabaseName", "Orleans")
            .WithEnvironment("Orleans__Clustering__ContainerName", "OrleansCluster")
            .WithEnvironment("Orleans__GrainStorage__Default__DatabaseName", "Orleans")
            .WithEnvironment("Orleans__GrainStorage__Default__ContainerName", "OrleansStorage")
            .WithEnvironment("Orleans__Reminders__DatabaseName", "Orleans")
            .WithEnvironment("Orleans__Reminders__ContainerName", "OrleansReminders")
            .WaitFor(cosmos);

        builder.Build().Run();
    }
    // </cosmos_providers_aspire>

    // <redis_providers_aspire>
    public static void RedisProvidersWithAspire(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        var redis = builder.AddAzureManagedRedis("orleans-redis")
            .RunAsContainer();

        var orleans = builder.AddOrleans("cluster")
            .WithClustering(redis)
            .WithGrainStorage("Default", redis)
            .WithReminders(redis)
            .WithGrainDirectory("directory", redis);

        builder.AddProject<Projects.Silo>("silo")
            .WithReference(orleans)
            .WaitFor(redis);

        builder.Build().Run();
    }
    // </redis_providers_aspire>

    // <redis_journaling_aspire>
    public static void RedisJournalingWithAspire(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        var redis = builder.AddRedis("orleans-redis");
        var orleans = builder.AddOrleans("cluster")
            .WithClustering(redis);

        builder.AddProject<Projects.Silo>("silo")
            .WithReference(orleans)
            .WithEnvironment("Orleans__GrainJournaling__ProviderType", "Redis")
            .WithEnvironment("Orleans__GrainJournaling__ServiceKey", "orleans-redis")
            .WaitFor(redis);

        builder.Build().Run();
    }
    // </redis_journaling_aspire>

    // <azure_table_journaling_aspire>
    public static void AzureTableJournalingWithAspire(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        var storage = builder.AddAzureStorage("orleans-storage")
            .RunAsEmulator();
        var tables = storage.AddTables("orleans-tables");
        var orleans = builder.AddOrleans("cluster")
            .WithClustering(tables);

        builder.AddProject<Projects.Silo>("silo")
            .WithReference(orleans)
            .WithEnvironment("Orleans__GrainJournaling__ProviderType", "AzureTableStorage")
            .WithEnvironment("Orleans__GrainJournaling__ServiceKey", "orleans-tables")
            .WaitFor(storage);

        builder.Build().Run();
    }
    // </azure_table_journaling_aspire>

    // <event_hubs_aspire>
    public static void EventHubsWithAspire(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        var eventHubs = builder.AddAzureEventHubs("event-hubs");
        var consumerGroup = eventHubs
            .AddHub("orders-hub", "orders")
            .AddConsumerGroup("orders-consumer", "orleans");
        var storage = builder.AddAzureStorage("checkpoints");
        var tables = storage.AddTables("checkpoint-tables");
        var orleans = builder.AddOrleans("cluster")
            .WithDevelopmentClustering()
            .WithStreaming("orders", consumerGroup);

        builder.AddProject<Projects.Silo>("silo")
            .WithReference(orleans)
            .WithReference(tables)
            .WithEnvironment("Orleans__Streaming__orders__CheckpointerServiceKey", "checkpoint-tables")
            .WaitFor(eventHubs)
            .WaitFor(storage);

        builder.Build().Run();
    }
    // </event_hubs_aspire>

    // <dynamodb_local_aspire>
    public static void DynamoDBLocalWithAspire(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        var dynamodb = builder.AddAWSDynamoDBLocal("dynamodb");
        var provider = new DynamoDBProviderConfiguration(dynamodb.Resource.Name);
        var orleans = builder.AddOrleans("cluster")
            .WithClusterId("orders")
            .WithServiceId("orders")
            .WithClustering(provider)
            .WithGrainStorage("Default", provider)
            .WithReminders(provider);

        builder.AddProject<Projects.Silo>("silo")
            .WithReference(orleans)
            .WithReference(dynamodb)
            .WaitFor(dynamodb);

        builder.Build().Run();
    }

    private sealed class DynamoDBProviderConfiguration(string serviceKey) : IProviderConfiguration
    {
        public void ConfigureResource<T>(
            IResourceBuilder<T> resourceBuilder,
            string configSectionPath)
            where T : IResourceWithEnvironment
        {
            var prefix = $"Orleans__{configSectionPath.Replace(":", "__", StringComparison.Ordinal)}";
            resourceBuilder
                .WithEnvironment($"{prefix}__ProviderType", "DynamoDB")
                .WithEnvironment($"{prefix}__ServiceKey", serviceKey);
        }
    }
    // </dynamodb_local_aspire>

    // <local_development>
    public static void LocalDevelopment(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        var redis = builder.AddRedis("orleans-redis");
        // Redis container runs automatically during development

        builder.AddOrleans("cluster")
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

        builder.AddOrleans("cluster")
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

    // <kinesis_apphost_grain_checkpoints>
    public static void KinesisWithGrainCheckpoints(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        var aws = builder.AddAWSSDKConfig()
            .WithRegion(RegionEndpoint.USWest2);
        var stack = builder.AddAWSCDKStack("streaming")
            .WithReference(aws);
        var stream = stack.AddKinesisStream("orders-stream");

        builder.AddProject<Projects.Silo>("silo")
            .WithReference(stream)
            .WithEnvironment("Orleans__Streaming__Orders__ProviderType", "Kinesis")
            .WithEnvironment("Orleans__Streaming__Orders__ServiceKey", stream.Resource.Name)
            .WithEnvironment("Orleans__Streaming__Orders__Checkpoint__Type", "Grain");

        builder.AddProject<Projects.Client>("client")
            .WithReference(stream)
            .WithEnvironment("Orleans__Streaming__Orders__ProviderType", "Kinesis")
            .WithEnvironment("Orleans__Streaming__Orders__ServiceKey", stream.Resource.Name);

        builder.Build().Run();
    }
    // </kinesis_apphost_grain_checkpoints>

    // <kinesis_apphost_dynamodb_checkpoints>
    public static void KinesisWithDynamoDBCheckpoints(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        var aws = builder.AddAWSSDKConfig()
            .WithRegion(RegionEndpoint.USWest2);
        var stack = builder.AddAWSCDKStack("streaming")
            .WithReference(aws);
        var stream = stack.AddKinesisStream("orders-stream");
        var checkpoints = stack.AddDynamoDBTable(
            "orders-checkpoints",
            new TableProps
            {
                BillingMode = BillingMode.PAY_PER_REQUEST,
                PartitionKey = new Amazon.CDK.AWS.DynamoDB.Attribute
                {
                    Name = "CheckpointNamespace",
                    Type = AttributeType.STRING,
                },
                SortKey = new Amazon.CDK.AWS.DynamoDB.Attribute
                {
                    Name = "Partition",
                    Type = AttributeType.STRING,
                },
            });

        builder.AddProject<Projects.Silo>("silo")
            .WithReference(stream)
            .WithReference(checkpoints)
            .WithEnvironment("Orleans__Streaming__Orders__ProviderType", "Kinesis")
            .WithEnvironment("Orleans__Streaming__Orders__ServiceKey", stream.Resource.Name)
            .WithEnvironment("Orleans__Streaming__Orders__Checkpoint__Type", "DynamoDB")
            .WithEnvironment("Orleans__Streaming__Orders__Checkpoint__ServiceKey", checkpoints.Resource.Name)
            .WithEnvironment("Orleans__Streaming__Orders__Checkpoint__CreateIfNotExists", "false");

        builder.AddProject<Projects.Client>("client")
            .WithReference(stream)
            .WithEnvironment("Orleans__Streaming__Orders__ProviderType", "Kinesis")
            .WithEnvironment("Orleans__Streaming__Orders__ServiceKey", stream.Resource.Name);

        builder.Build().Run();
    }
    // </kinesis_apphost_dynamodb_checkpoints>

    // <adonet_apphost>
    public static void AdoNetAppHost(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        var database = builder.AddSqlServer("sql")
            .AddDatabase("orleans-db")
            .WithCreationScript(File.ReadAllText("schema/sqlserver.sql"));

        var orleans = builder.AddOrleans("cluster")
            .WithClustering(database)
            .WithGrainStorage("Default", database)
            .WithReminders(database)
            .WithStreaming("streams", database)
            .WithGrainDirectory("directory", database);

        builder.AddProject<Projects.Silo>("silo")
            .WithReference(orleans)
            .WaitFor(database);

        builder.Build().Run();
    }
    // </adonet_apphost>

    // <adonet_postgresql_apphost>
    public static void AdoNetPostgreSqlAppHost(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        var postgres = builder.AddPostgres("postgres")
            .WithInitFiles("schema/postgresql");
        var database = postgres.AddDatabase("orleans-db");

        var orleans = builder.AddOrleans("cluster")
            .WithClustering(database)
            .WithGrainStorage("Default", database)
            .WithReminders(database)
            .WithStreaming("streams", database)
            .WithGrainDirectory("directory", database);

        builder.AddProject<Projects.Silo>("silo")
            .WithReference(orleans)
            .WaitFor(database);

        builder.Build().Run();
    }
    // </adonet_postgresql_apphost>

    // <adonet_mysql_apphost>
    public static void AdoNetMySqlAppHost(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        var mysql = builder.AddMySql("mysql")
            .WithInitFiles("schema/mysql");
        var database = mysql.AddDatabase("orleans-db");

        var orleans = builder.AddOrleans("cluster")
            .WithClustering(database)
            .WithGrainStorage("Default", database)
            .WithReminders(database)
            .WithStreaming("streams", database)
            .WithGrainDirectory("directory", database);

        builder.AddProject<Projects.Silo>("silo")
            .WithReference(orleans)
            .WaitFor(database);

        builder.Build().Run();
    }
    // </adonet_mysql_apphost>

    // <adonet_oracle_apphost>
    public static void AdoNetOracleAppHost(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        var oracle = builder.AddOracle("oracle")
            .WithInitFiles("schema/oracle");
        var database = oracle.AddDatabase("orleans-db");

        var orleans = builder.AddOrleans("cluster")
            .WithClustering(database)
            .WithGrainStorage("Default", database)
            .WithReminders(database);

        builder.AddProject<Projects.Silo>("silo")
            .WithReference(orleans)
            .WaitFor(database);

        builder.Build().Run();
    }
    // </adonet_oracle_apphost>

    // <adonet_explicit_provider>
    public static void ExplicitAdoNetProviderAppHost(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        var database = builder.AddConnectionString("orders-db");
        var provider = new ExplicitAdoNetProviderConfiguration(database, "Npgsql");

        var orleans = builder.AddOrleans("cluster")
            .WithClustering(provider)
            .WithGrainStorage("Default", provider)
            .WithReminders(provider);

        builder.AddProject<Projects.Silo>("silo")
            .WithReference(orleans);

        builder.Build().Run();
    }
    // </adonet_explicit_provider>

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

    private sealed class ExplicitAdoNetProviderConfiguration(
        IResourceBuilder<IResourceWithConnectionString> database,
        string invariant) : IProviderConfiguration
    {
        public void ConfigureResource<T>(
            IResourceBuilder<T> resourceBuilder,
            string configurationSectionPath)
            where T : IResourceWithEnvironment
        {
            var prefix = configurationSectionPath.Replace(":", "__", StringComparison.Ordinal);
            resourceBuilder
                .WithEnvironment($"Orleans__{prefix}__ProviderType", "AdoNet")
                .WithEnvironment($"Orleans__{prefix}__Invariant", invariant)
                .WithEnvironment($"Orleans__{prefix}__ServiceKey", database.Resource.Name)
                .WithReference(database);
        }
    }
}
