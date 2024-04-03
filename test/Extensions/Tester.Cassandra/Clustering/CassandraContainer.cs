using System.Net;
using Cassandra;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Tester.Cassandra.Clustering;

public class CassandraContainer
{
    public Task<(IContainer container, ushort exposedPort, Cluster cluster, ISession session)> RunImage() => _innerRunImage.Value;

    private readonly Lazy<Task<(IContainer container, ushort exposedPort, Cluster cluster, ISession session)>> _innerRunImage =
        new(async () =>
        {
            var cassandraImage = new ImageFromDockerfileBuilder()
                .WithDockerfileDirectory(CommonDirectoryPath.GetProjectDirectory(Directory.GetCurrentDirectory()), string.Empty)
                .WithDockerfile("Cassandra.dockerfile")
                .WithBuildArgument("CASSANDRAVERSION", Environment.GetEnvironmentVariable("CASSANDRAVERSION"))
                .Build();

            var imageTask = cassandraImage.CreateAsync();

            await imageTask;

            var containerPort = 9042;

            var builder = new ContainerBuilder()
                .WithImage(cassandraImage)
                .WithPortBinding(containerPort, true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(containerPort));

            var container = builder.Build();

            await container.StartAsync();

            var exposedPort = container.GetMappedPublicPort(containerPort);

            var cluster = Cluster.Builder()
                .WithDefaultKeyspace("orleans")
                .AddContactPoints(new IPEndPoint(IPAddress.Loopback, exposedPort))
                .Build();

            // Connect to the nodes using a keyspace
            var session =
                cluster.ConnectAndCreateDefaultKeyspaceIfNotExists(ReplicationStrategies
                    .CreateSimpleStrategyReplicationProperty(1));

            return (container, exposedPort, cluster, session);
        });

    public string Name { get; set; } = string.Empty;
}