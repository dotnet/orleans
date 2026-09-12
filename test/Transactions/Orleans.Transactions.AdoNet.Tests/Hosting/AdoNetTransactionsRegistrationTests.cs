using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Transactions.AdoNet.Storage;
using Orleans.Transactions.AdoNet.TransactionalState;
using Xunit;

namespace Orleans.Transactions.AdoNet.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Transactions")]
public sealed class AdoNetTransactionsRegistrationTests
{
    [Fact]
    public void AddAdoNetTransactionalStateStorage_RegistersNamedOptionsAndSql()
    {
        var builder = new TestSiloBuilder();

        var result = builder.AddAdoNetTransactionalStateStorage(
            "transactions",
            options =>
            {
                options.Invariant = "custom-invariant";
                options.StateEntityTableName = "CustomState";
                options.KeyEntityTableName = "CustomKey";
            });

        using var services = builder.Services.BuildServiceProvider();
        var options = services.GetRequiredService<IOptionsMonitor<TransactionalStateStorageOptions>>().Get("transactions");

        Assert.Same(builder, result);
        Assert.Equal("custom-invariant", options.Invariant);
        Assert.Equal("CustomState", options.StateEntityTableName);
        Assert.Equal("CustomKey", options.KeyEntityTableName);
        Assert.Equal(8, options.ExecuteSqlDictionary.Count);
        Assert.Contains("CustomState", options.ExecuteSqlDictionary[Utils.Constants.QueryStateSql]);
        Assert.Contains("CustomKey", options.ExecuteSqlDictionary[Utils.Constants.QueryKeySql]);
    }

    [Fact]
    public void AddAdoNetTransactionalStateStorage_OracleUsesColonParameterPrefix()
    {
        var builder = new TestSiloBuilder();

        builder.AddAdoNetTransactionalStateStorage(
            "oracle-transactions",
            options =>
            {
                options.Invariant = AdoNetInvariants.InvariantNameOracleDatabase;
            });

        using var services = builder.Services.BuildServiceProvider();
        var options = services.GetRequiredService<IOptionsMonitor<TransactionalStateStorageOptions>>().Get("oracle-transactions");

        Assert.Equal(Utils.Constants.OracleParameterDot, options.SqlParameterDot);
        Assert.All(options.ExecuteSqlDictionary.Values, sql => Assert.DoesNotContain("@", sql));
        Assert.Contains(":", options.ExecuteSqlDictionary[Utils.Constants.AddStateSql]);
    }

    [Fact]
    public void AddAdoNetTransactionalStateStorageAsDefault_UsesDefaultProviderName()
    {
        var builder = new TestSiloBuilder();

        builder.AddAdoNetTransactionalStateStorageAsDefault(options => options.StateIdKeyMaxLength = 64);

        using var services = builder.Services.BuildServiceProvider();
        var options = services.GetRequiredService<IOptionsMonitor<TransactionalStateStorageOptions>>()
            .Get(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME);

        Assert.Equal(64, options.StateIdKeyMaxLength);
        Assert.Equal(8, options.ExecuteSqlDictionary.Count);
    }

    [Fact]
    public void AddAdoNetTransactionalStateStorage_WithoutConfiguration_UsesDefaults()
    {
        var builder = new TestSiloBuilder();

        AdoNetTransactionsSiloBuilderExtensions.AddAdoNetTransactionalStateStorage(builder, "transactions", null);

        using var services = builder.Services.BuildServiceProvider();
        var options = services.GetRequiredService<IOptionsMonitor<TransactionalStateStorageOptions>>().Get("transactions");

        Assert.Equal(TransactionalStateStorageOptions.DEFAULT_ADONET_INVARIANT, options.Invariant);
        Assert.Equal(8, options.ExecuteSqlDictionary.Count);
    }

    [Fact]
    public void AddAdoNetTransactionalStateStorage_RegistersNamedConfigurationValidator()
    {
        var builder = new TestSiloBuilder();
        builder.AddAdoNetTransactionalStateStorage(
            "transactions",
            options => options.ConnectionString = "Server=localhost;Database=orleans");

        using var services = builder.Services.BuildServiceProvider();
        var validator = Assert.Single(services.GetServices<IConfigurationValidator>());

        validator.ValidateConfiguration();
    }

    [Theory]
    [InlineData(AdoNetInvariants.InvariantNameMySql)]
    [InlineData(AdoNetInvariants.InvariantNameMySqlConnector)]
    [InlineData(AdoNetInvariants.InvariantNamePostgreSql)]
    [InlineData(AdoNetInvariants.InvariantNameOracleDatabase)]
    public void AddAdoNetTransactionalStateStorage_SupportedInvariantPassesValidation(string invariant)
    {
        var builder = new TestSiloBuilder();
        builder.AddAdoNetTransactionalStateStorage(
            "transactions",
            options =>
            {
                options.Invariant = invariant;
                options.ConnectionString = "Data Source=transactions";
            });

        using var services = builder.Services.BuildServiceProvider();
        var validator = Assert.Single(services.GetServices<IConfigurationValidator>());

        validator.ValidateConfiguration();
    }

    [Fact]
    public void AddAdoNetTransactionalStateStorage_SqliteInvariantFailsValidation()
    {
        var builder = new TestSiloBuilder();
        builder.AddAdoNetTransactionalStateStorage(
            "transactions",
            options =>
            {
                options.Invariant = AdoNetInvariants.InvariantNameSqlLite;
                options.ConnectionString = "Data Source=transactions.db";
            });

        using var services = builder.Services.BuildServiceProvider();
        var validator = Assert.Single(services.GetServices<IConfigurationValidator>());

        var exception = Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);
        Assert.Contains(AdoNetInvariants.InvariantNameSqlLite, exception.Message);
    }

    [Fact]
    public void AddAdoNetTransactionalStateStorage_MissingConnectionStringFailsValidation()
    {
        var builder = new TestSiloBuilder();
        AdoNetTransactionsSiloBuilderExtensions.AddAdoNetTransactionalStateStorage(builder, "transactions", null);

        using var services = builder.Services.BuildServiceProvider();
        var validator = Assert.Single(services.GetServices<IConfigurationValidator>());

        var exception = Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);
        Assert.Contains(nameof(TransactionalStateStorageOptions.ConnectionString), exception.Message);
    }

    [Fact]
    public void AddAdoNetTransactionalStateStorage_ShortStateIdLimitFailsValidation()
    {
        var builder = new TestSiloBuilder();
        builder.AddAdoNetTransactionalStateStorage(
            "transactions",
            options =>
            {
                options.ConnectionString = "Server=localhost;Database=orleans";
                options.StateIdKeyMaxLength = TransactionalStateStorageFactory.StateIdLength - 1;
            });

        using var services = builder.Services.BuildServiceProvider();
        var validator = Assert.Single(services.GetServices<IConfigurationValidator>());

        var exception = Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);
        Assert.Contains(nameof(TransactionalStateStorageOptions.StateIdKeyMaxLength), exception.Message);
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes("{}")))
            .Build();
    }
}
