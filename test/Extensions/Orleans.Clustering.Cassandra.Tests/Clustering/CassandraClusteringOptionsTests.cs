using System;
using Orleans.Clustering.Cassandra.Hosting;
using Xunit;

namespace Orleans.Clustering.Cassandra.Tests.Clustering;

public sealed class CassandraClusteringOptionsTests
{
    [Fact]
    public void ProviderOwnedClientNormalizesKeyspaceBeforeConnecting()
    {
        var options = new CassandraClusteringOptions();

        options.ConfigureClient("Contact Points=127.0.0.1", "StateKeyspace");

        Assert.Equal("statekeyspace", options.Keyspace);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1keyspace")]
    [InlineData("_keyspace")]
    [InlineData("keyspace-name")]
    [InlineData("keyspace.name")]
    [InlineData("\"keyspace\"")]
    [InlineData("keyspace'")]
    public void ProviderOwnedClientRejectsInvalidKeyspaceBeforeConnecting(string keyspace)
    {
        var options = new CassandraClusteringOptions();

        Assert.Throws<ArgumentException>(() => options.ConfigureClient("Contact Points=127.0.0.1", keyspace));
    }

    [Fact]
    public void ProviderOwnedClientRejectsOverlongKeyspaceBeforeConnecting()
    {
        var options = new CassandraClusteringOptions();

        Assert.Throws<ArgumentException>(() => options.ConfigureClient("Contact Points=127.0.0.1", new string('a', 49)));
    }
}
