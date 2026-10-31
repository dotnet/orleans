using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.DurableJobs;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Journaling.Json;
using TestExtensions;
using Xunit;

namespace Tester.Cassandra.Journaling;

[TestSuite("BVT")]
[TestProvider("Cassandra")]
[TestArea("DurableJobs")]
[TestCategory("BVT")]
[TestCategory("Cassandra")]
public sealed class CassandraDurableJobsConfigurationTests
{
    [Fact]
    public void CassandraJournalStorageOptions_RejectsCatalogTableNameResolvingToJournalTable()
    {
        AssertCatalogTableNameRejected("same_table", "same_table");
        AssertCatalogTableNameRejected("Journal", "journal");

        static void AssertCatalogTableNameRejected(string tableName, string catalogTableName)
        {
            var options = new CassandraJournalStorageOptions
            {
                TableName = tableName,
                CatalogTableName = catalogTableName,
            };

            Assert.Throws<ArgumentException>(options.Validate);
        }
    }

    [Fact]
    public void CassandraJournalStorageAssemblyIsMarkedExperimental()
    {
        var attribute = typeof(CassandraJournalStorageOptions).Assembly.GetCustomAttribute<ExperimentalAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal("ORLEANSEXP005", attribute.DiagnosticId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UseCassandraDurableJobs_RegistersJournaledDurableJobs(bool useServiceCollection)
    {
        var builder = new TestSiloBuilder();
        static void Configure(CassandraJournalStorageOptions options)
        {
            options.TableName = "durable_jobs_test";
            options.CatalogTableName = "durable_jobs_catalog_test";
            options.ConfigureClient(_ => throw new InvalidOperationException("The Cassandra session is not needed for this test."));
        }

        if (useServiceCollection)
        {
            Assert.Same(builder.Services, builder.Services.UseCassandraDurableJobs(Configure));
        }
        else
        {
            Assert.Same(builder, builder.UseCassandraDurableJobs(Configure));
        }

        Assert.Contains(builder.Services, descriptor => descriptor.ServiceType == typeof(IJournalStorageProvider));
        Assert.Contains(builder.Services, descriptor => descriptor.ServiceType == typeof(IJournalStorageCatalog));
        using var serviceProvider = builder.Services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<IOptions<CassandraJournalStorageOptions>>().Value;
        Assert.Equal("durable_jobs_test", options.TableName);
        Assert.Equal("durable_jobs_catalog_test", options.CatalogTableName);
        Assert.Contains(builder.Services, descriptor => descriptor.ServiceType == typeof(IDurableJobsStorageInspector));
        var jsonOptions = serviceProvider.GetRequiredService<IOptions<JsonJournalOptions>>().Value;
        var durableJobsJsonContextType = typeof(DurableJob).Assembly.GetType("Orleans.DurableJobs.DurableJobsJsonContext", throwOnError: true)!;
        Assert.Contains(jsonOptions.SerializerOptions.TypeInfoResolverChain, resolver => durableJobsJsonContextType.IsInstanceOfType(resolver));
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }
}
