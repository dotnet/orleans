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
            var containerPort = 9042;

            var container = new ContainerBuilder()
                    .WithImage("cassandra:" + Environment.GetEnvironmentVariable("CASSANDRAVERSION"))
                    .WithPortBinding(containerPort, true)
                    .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(containerPort))
                    .Build();

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
