using Amazon;
using Amazon.CDK.AWS.DynamoDB;
using Amazon.CDK.AWS.Kinesis;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.AWS;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Orleans;
using CdkDuration = Amazon.CDK.Duration;
using CdkRemovalPolicy = Amazon.CDK.RemovalPolicy;
using DynamoDBAttribute = Amazon.CDK.AWS.DynamoDB.Attribute;

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

        var dynamodb = builder.AddConnectionString("dynamodb");
        var provider = new DynamoDBProviderConfiguration(null, dynamodb.Resource.Name);
        var orleans = builder.AddOrleans("cluster")
            .WithClusterId("orders")
            .WithServiceId("orders")
            .WithClustering(provider)
            .WithGrainStorage("Default", provider)
            .WithReminders(provider);

        builder.AddProject<Projects.Silo>("silo")
            .WithReference(orleans)
            .WithReference(dynamodb);

        builder.Build().Run();
    }
    // </dynamodb_local_aspire>

    // <dynamodb_cdk_aspire>
    public static void DynamoDBWithAwsCdk(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        var aws = builder.AddAWSSDKConfig()
            .WithRegion(RegionEndpoint.USEast1);
        var stack = builder.AddAWSCDKStack("orleans-dynamodb")
            .WithReference(aws);
        var membership = stack.AddDynamoDBTable(
            DynamoDBTopology.MembershipResourceName,
            DynamoDBTopology.CreateMembershipTable());
        var grainState = stack.AddDynamoDBTable(
            DynamoDBTopology.GrainStateResourceName,
            DynamoDBTopology.CreateGrainStateTable());
        var reminders = stack.AddDynamoDBTable(
            DynamoDBTopology.RemindersResourceName,
            DynamoDBTopology.CreateRemindersTable())
            .AddGlobalSecondaryIndex(DynamoDBTopology.CreateServiceIdIndex())
            .AddGlobalSecondaryIndex(DynamoDBTopology.CreateServiceIdGrainReferenceIndex());
        var transactions = stack.AddDynamoDBTable(
            DynamoDBTopology.TransactionsResourceName,
            DynamoDBTopology.CreateTransactionsTable());
        var checkpoints = stack.AddDynamoDBTable(
            DynamoDBTopology.CheckpointsResourceName,
            DynamoDBTopology.CreateCheckpointsTable());

        var orleans = builder.AddOrleans("cluster")
            .WithClusterId(DynamoDBTopology.ClusterId)
            .WithServiceId(DynamoDBTopology.ServiceId)
            .WithClustering(
                new DynamoDBProviderConfiguration(
                    aws,
                    DynamoDBTopology.MembershipResourceName,
                    infrastructureOwnsTable: true))
            .WithGrainStorage(
                "Default",
                new DynamoDBProviderConfiguration(
                    aws,
                    DynamoDBTopology.GrainStateResourceName,
                    infrastructureOwnsTable: true,
                    serviceId: DynamoDBTopology.ServiceId))
            .WithReminders(
                new DynamoDBProviderConfiguration(
                    aws,
                    DynamoDBTopology.RemindersResourceName,
                    infrastructureOwnsTable: true));

        var silo = builder.AddProject<Projects.Silo>("silo")
            .WithReference(orleans)
            .WithReference(membership)
            .WithReference(grainState)
            .WithReference(reminders)
            .WithReference(transactions)
            .WithReference(checkpoints)
            .WaitFor(stack)
            .WithReplicas(3);

        builder.AddProject<Projects.Client>("client")
            .WithReference(orleans.AsClient())
            .WithReference(membership)
            .WaitFor(stack)
            .WaitFor(silo);

        builder.Build().Run();
    }
    // </dynamodb_cdk_aspire>

    // <dynamodb_provider_configuration>
    private sealed class DynamoDBProviderConfiguration(
        IAWSSDKConfig? aws,
        string serviceKey,
        bool infrastructureOwnsTable = false,
        string? serviceId = null) : IProviderConfiguration
    {
        public void ConfigureResource<T>(
            IResourceBuilder<T> resourceBuilder,
            string configSectionPath)
            where T : IResourceWithEnvironment
        {
            var prefix = $"Orleans__{configSectionPath.Replace(":", "__", StringComparison.Ordinal)}";
            if (aws is not null)
            {
                var region = aws.Region?.SystemName
                    ?? throw new InvalidOperationException("DynamoDB providers require an AWS region.");
                resourceBuilder
                    .WithReference(aws)
                    .WithEnvironment($"{prefix}__Region", region);
            }

            resourceBuilder
                .WithEnvironment($"{prefix}__ProviderType", "DynamoDB")
                .WithEnvironment($"{prefix}__ServiceKey", serviceKey);
            if (infrastructureOwnsTable)
            {
                resourceBuilder
                    .WithEnvironment($"{prefix}__UseProvisionedThroughput", "false")
                    .WithEnvironment($"{prefix}__CreateIfNotExists", "false")
                    .WithEnvironment($"{prefix}__UpdateIfExists", "false");
            }

            if (serviceId is not null)
            {
                resourceBuilder.WithEnvironment($"{prefix}__ServiceId", serviceId);
            }
        }
    }
    // </dynamodb_provider_configuration>

    // <dynamodb_cdk_topology>
    private static class DynamoDBTopology
    {
        public const string ClusterId = "orders-production";
        public const string ServiceId = "orders-service";
        public const string MembershipResourceName = "orleans-membership";
        public const string GrainStateResourceName = "orleans-grain-state";
        public const string RemindersResourceName = "orleans-reminders";
        public const string TransactionsResourceName = "orleans-transactions";
        public const string CheckpointsResourceName = "orleans-checkpoints";

        private const string MembershipTableName = "orders-orleans-membership";
        private const string GrainStateTableName = "orders-orleans-grain-state";
        private const string RemindersTableName = "orders-orleans-reminders";
        private const string TransactionsTableName = "orders-orleans-transactions";
        private const string CheckpointsTableName = "orders-orleans-checkpoints";

        public static TableProps CreateMembershipTable()
            => CreateTable(
                MembershipTableName,
                partitionKey: ("DeploymentId", AttributeType.STRING),
                sortKey: ("SiloIdentity", AttributeType.STRING));

        public static TableProps CreateGrainStateTable()
            => CreateTable(
                GrainStateTableName,
                partitionKey: ("GrainReference", AttributeType.STRING),
                sortKey: ("GrainType", AttributeType.STRING));

        public static TableProps CreateRemindersTable()
            => CreateTable(
                RemindersTableName,
                partitionKey: ("ReminderId", AttributeType.STRING),
                sortKey: ("GrainHash", AttributeType.NUMBER));

        public static GlobalSecondaryIndexProps CreateServiceIdIndex()
            => CreateIndex(
                "ServiceIdIndex",
                partitionKey: ("ServiceId", AttributeType.STRING),
                sortKey: ("GrainHash", AttributeType.NUMBER));

        public static GlobalSecondaryIndexProps CreateServiceIdGrainReferenceIndex()
            => CreateIndex(
                "ServiceIdGrainReferenceIndex",
                partitionKey: ("ServiceId", AttributeType.STRING),
                sortKey: ("GrainReference", AttributeType.STRING));

        public static TableProps CreateTransactionsTable()
            => CreateTable(
                TransactionsTableName,
                partitionKey: ("PartitionKey", AttributeType.STRING),
                sortKey: ("RowKey", AttributeType.STRING));

        public static TableProps CreateCheckpointsTable()
            => CreateTable(
                CheckpointsTableName,
                partitionKey: ("CheckpointNamespace", AttributeType.STRING),
                sortKey: ("Partition", AttributeType.STRING));

        private static TableProps CreateTable(
            string tableName,
            (string Name, AttributeType Type) partitionKey,
            (string Name, AttributeType Type) sortKey)
            => new()
            {
                TableName = tableName,
                BillingMode = BillingMode.PAY_PER_REQUEST,
                PartitionKey = CreateAttribute(partitionKey),
                SortKey = CreateAttribute(sortKey),
            };

        private static GlobalSecondaryIndexProps CreateIndex(
            string indexName,
            (string Name, AttributeType Type) partitionKey,
            (string Name, AttributeType Type) sortKey)
            => new()
            {
                IndexName = indexName,
                PartitionKey = CreateAttribute(partitionKey),
                SortKey = CreateAttribute(sortKey),
                ProjectionType = ProjectionType.ALL,
            };

        private static DynamoDBAttribute CreateAttribute((string Name, AttributeType Type) attribute)
            => new()
            {
                Name = attribute.Name,
                Type = attribute.Type,
            };
    }
    // </dynamodb_cdk_topology>

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

    // <kinesis_streaming_apphost>
    public static void KinesisStreaming(string[] args)
    {
        var topology = KinesisTopology.Orders;
        var builder = DistributedApplication.CreateBuilder(args);

        var aws = builder.AddAWSSDKConfig()
            .WithRegion(topology.Region);
        var stack = builder.AddAWSCDKStack(topology.StackName)
            .WithReference(aws);
        var stream = stack.AddKinesisStream(
            topology.StreamResourceName,
            new StreamProps
            {
                StreamName = topology.StreamName,
                ShardCount = topology.ShardCount,
                StreamMode = StreamMode.PROVISIONED,
                RetentionPeriod = CdkDuration.Hours(topology.RetentionHours),
                RemovalPolicy = CdkRemovalPolicy.RETAIN,
            });
        var pubSubStore = stack.AddDynamoDBTable(
            topology.PubSubResourceName,
            new TableProps
            {
                TableName = topology.PubSubTableName,
                BillingMode = BillingMode.PAY_PER_REQUEST,
                RemovalPolicy = CdkRemovalPolicy.RETAIN,
                PartitionKey = new Amazon.CDK.AWS.DynamoDB.Attribute
                {
                    Name = "GrainReference",
                    Type = AttributeType.STRING,
                },
                SortKey = new Amazon.CDK.AWS.DynamoDB.Attribute
                {
                    Name = "GrainType",
                    Type = AttributeType.STRING,
                },
            });
        var checkpoints = stack.AddDynamoDBTable(
            topology.CheckpointResourceName,
            new TableProps
            {
                TableName = topology.CheckpointTableName,
                BillingMode = BillingMode.PAY_PER_REQUEST,
                RemovalPolicy = CdkRemovalPolicy.RETAIN,
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
        var orleans = builder.AddOrleans("cluster")
            .WithClusterId(topology.ClusterId)
            .WithServiceId(topology.ServiceId)
            .WithDevelopmentClustering()
            .WithGrainStorage(
                "PubSubStore",
                new DynamoDBProviderConfiguration(
                    aws,
                    pubSubStore.Resource.Name,
                    infrastructureOwnsTable: true,
                    serviceId: topology.ServiceId))
            .WithStreaming(
                topology.ProviderName,
                new KinesisProviderConfiguration(
                    aws,
                    topology,
                    stream.Resource.Name,
                    checkpoints.Resource.Name));

        var silo = builder.AddProject<Projects.Silo>("silo")
            .WithReference(orleans)
            .WithReference(stream)
            .WithReference(pubSubStore)
            .WithReference(checkpoints)
            .WaitFor(stack)
            .WithReplicas(3);

        builder.AddProject<Projects.Client>("client")
            .WithReference(orleans.AsClient())
            .WithReference(stream)
            .WaitFor(stack)
            .WaitFor(silo);

        builder.Build().Run();
    }
    // </kinesis_streaming_apphost>

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

    // <kinesis_provider_configuration>
    private sealed class KinesisProviderConfiguration(
        IAWSSDKConfig aws,
        KinesisTopology topology,
        string streamServiceKey,
        string checkpointServiceKey) : IProviderConfiguration
    {
        public void ConfigureResource<T>(
            IResourceBuilder<T> resourceBuilder,
            string configurationSectionPath)
            where T : IResourceWithEnvironment
        {
            var prefix = $"Orleans__{configurationSectionPath.Replace(":", "__", StringComparison.Ordinal)}";
            var region = aws.Region?.SystemName
                ?? throw new InvalidOperationException("Kinesis streaming requires an AWS region.");
            resourceBuilder
                .WithReference(aws)
                .WithEnvironment($"{prefix}__ProviderType", "Kinesis")
                .WithEnvironment($"{prefix}__ServiceKey", streamServiceKey)
                .WithEnvironment($"{prefix}__StreamName", topology.StreamName)
                .WithEnvironment($"{prefix}__Region", region)
                .WithEnvironment($"{prefix}__Checkpoint__Type", "DynamoDB")
                .WithEnvironment($"{prefix}__Checkpoint__ServiceKey", checkpointServiceKey)
                .WithEnvironment($"{prefix}__Checkpoint__Region", region)
                .WithEnvironment($"{prefix}__Checkpoint__CreateIfNotExists", "false")
                .WithEnvironment($"{prefix}__Checkpoint__UseProvisionedThroughput", "false");
        }
    }
    // </kinesis_provider_configuration>

    // <kinesis_topology>
    private sealed record KinesisTopology(
        string StackName,
        string ClusterId,
        string ServiceId,
        string ProviderName,
        RegionEndpoint Region,
        string StreamResourceName,
        string StreamName,
        int ShardCount,
        int RetentionHours,
        string PubSubResourceName,
        string PubSubTableName,
        string CheckpointResourceName,
        string CheckpointTableName)
    {
        public static KinesisTopology Orders { get; } = new(
            StackName: "orders-kinesis",
            ClusterId: "orders-v1",
            ServiceId: "orders-service",
            ProviderName: "Orders",
            Region: RegionEndpoint.USWest2,
            StreamResourceName: "orders-stream",
            StreamName: "orleans-orders",
            ShardCount: 4,
            RetentionHours: 24,
            PubSubResourceName: "orders-pubsub",
            PubSubTableName: "orleans-orders-pubsub",
            CheckpointResourceName: "orders-checkpoints",
            CheckpointTableName: "orleans-orders-checkpoints");
    }
    // </kinesis_topology>
}
