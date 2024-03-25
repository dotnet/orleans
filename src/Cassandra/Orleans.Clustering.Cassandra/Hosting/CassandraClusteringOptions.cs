namespace Orleans.Clustering.Cassandra.Hosting;

public class CassandraClusteringOptions
{
    public required string ConnectionString { get; set; }
    public string Keyspace { get; set; } = "orleans";
}