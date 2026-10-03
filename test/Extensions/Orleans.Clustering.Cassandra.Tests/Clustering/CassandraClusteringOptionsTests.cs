using System;
using Orleans.Clustering.Cassandra.Hosting;
using TestExtensions;
using Xunit;

namespace Orleans.Clustering.Cassandra.Tests.Clustering;

[TestProvider("Cassandra"), TestSuite("BVT"), TestCategory("BVT")]
public sealed class CassandraClusteringOptionsTests
{
    [Theory]
    [InlineData("StateKeyspace")]
    [InlineData("_keyspace")]
    [InlineData("keyspace-name")]
    [InlineData("keyspace.name")]
    public void ProviderOwnedClientPreservesKeyspaceBeforeConnecting(string keyspace)
    {
        var options = new CassandraClusteringOptions();

        options.ConfigureClient("Contact Points=127.0.0.1", keyspace);

        Assert.Equal(keyspace, options.Keyspace);
    }

    [Fact]
    public void ProviderOwnedClientRejectsNullKeyspace()
    {
        var options = new CassandraClusteringOptions();

        Assert.Throws<ArgumentNullException>(() => options.ConfigureClient("Contact Points=127.0.0.1", null!));
    }
}
