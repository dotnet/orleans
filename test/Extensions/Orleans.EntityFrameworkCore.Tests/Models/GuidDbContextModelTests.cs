using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Orleans.Clustering.EntityFrameworkCore.Data;
using Orleans.Clustering.EntityFrameworkCore.MySql.Data;
using Orleans.Clustering.EntityFrameworkCore.PostgreSQL.Data;
using Orleans.EntityFrameworkCore.Tests.Infrastructure;
using Orleans.GrainDirectory.EntityFrameworkCore.Data;
using Orleans.GrainDirectory.EntityFrameworkCore.MySql.Data;
using Orleans.GrainDirectory.EntityFrameworkCore.PostgreSQL.Data;
using Orleans.Persistence.EntityFrameworkCore.Data;
using Orleans.Persistence.EntityFrameworkCore.MySql.Data;
using Orleans.Persistence.EntityFrameworkCore.PostgreSQL.Data;
using Orleans.Reminders.EntityFrameworkCore.Data;
using Orleans.Reminders.EntityFrameworkCore.MySql.Data;
using Orleans.Reminders.EntityFrameworkCore.PostgreSQL.Data;
using TestExtensions;

namespace Orleans.EntityFrameworkCore.Tests.Models;

[TestCategory(EFCoreTestCategories.Unit)]
public sealed class GuidDbContextModelTests
{
    [Theory]
    [InlineData(DatabaseProvider.MySql, Feature.Clustering)]
    [InlineData(DatabaseProvider.MySql, Feature.GrainDirectory)]
    [InlineData(DatabaseProvider.MySql, Feature.Persistence)]
    [InlineData(DatabaseProvider.MySql, Feature.Reminders)]
    [InlineData(DatabaseProvider.PostgreSql, Feature.Clustering)]
    [InlineData(DatabaseProvider.PostgreSql, Feature.GrainDirectory)]
    [InlineData(DatabaseProvider.PostgreSql, Feature.Persistence)]
    [InlineData(DatabaseProvider.PostgreSql, Feature.Reminders)]
    public void ETag_IsApplicationManagedConcurrencyToken(DatabaseProvider provider, Feature feature)
    {
        using var context = CreateContext(provider, feature);
        var etags = context.Model.GetEntityTypes()
            .Select(entity => entity.FindProperty("ETag"))
            .Where(property => property is not null)
            .Cast<IProperty>()
            .ToArray();

        Assert.NotEmpty(etags);
        Assert.All(etags, property =>
        {
            Assert.Equal(typeof(Guid), property.ClrType);
            Assert.False(property.IsNullable);
            Assert.True(property.IsConcurrencyToken);
            Assert.Equal(ValueGenerated.Never, property.ValueGenerated);
            Assert.Equal(PropertySaveBehavior.Save, property.GetBeforeSaveBehavior());
            Assert.Equal(PropertySaveBehavior.Save, property.GetAfterSaveBehavior());
        });
    }

    [Theory]
    [InlineData(Feature.Clustering)]
    [InlineData(Feature.GrainDirectory)]
    [InlineData(Feature.Persistence)]
    [InlineData(Feature.Reminders)]
    public void MySql_ETag_UsesChar36AsciiCollation(Feature feature)
    {
        using var context = CreateContext(DatabaseProvider.MySql, feature);
        var etags = GetETagProperties(context.GetService<IDesignTimeModel>().Model);

        Assert.NotEmpty(etags);
        Assert.All(etags, property => Assert.Equal("char(36)", property.GetColumnType()));
        var createScript = context.Database.GenerateCreateScript();
        Assert.Equal(
            etags.Length,
            createScript.Split("char(36) COLLATE ascii_general_ci NOT NULL").Length - 1);
    }

    [Theory]
    [InlineData(Feature.Clustering)]
    [InlineData(Feature.GrainDirectory)]
    [InlineData(Feature.Persistence)]
    [InlineData(Feature.Reminders)]
    public void PostgreSql_ETag_UsesUuid(Feature feature)
    {
        using var context = CreateContext(DatabaseProvider.PostgreSql, feature);

        var etags = GetETagProperties(context.GetService<IDesignTimeModel>().Model);
        Assert.NotEmpty(etags);
        Assert.All(etags, property =>
        {
            Assert.Equal("uuid", property.GetColumnType());
            Assert.Null(property.GetDefaultValueSql());
            Assert.Null(property.GetComputedColumnSql());
        });
    }

    [Fact]
    public void ClusteringModel_HasExpectedKeysIndexesJsonConversionsAndValueComparers()
    {
        using var context = CreateContext(DatabaseProvider.PostgreSql, Feature.Clustering);
        var cluster = context.Model.FindEntityType(typeof(ClusterRecord<Guid>))!;
        var silo = context.Model.FindEntityType(typeof(SiloRecord<Guid>))!;

        Assert.Equal(["Id"], GetPropertyNames(cluster.FindPrimaryKey()!.Properties));
        Assert.Equal(
            ["ClusterId", "Address", "Port", "Generation"],
            GetPropertyNames(silo.FindPrimaryKey()!.Properties));
        AssertIndexSet(
            silo,
            ["ClusterId"],
            ["ClusterId", "Status"],
            ["ClusterId", "Status", "IAmAliveTime"]);

        foreach (var propertyName in new[] { "SuspectingTimes", "SuspectingSilos" })
        {
            var property = silo.FindProperty(propertyName)!;
            var converter = Assert.IsAssignableFrom<ValueConverter>(property.GetValueConverter());
            Assert.NotNull(property.GetValueComparer());
            Assert.Equal(typeof(string), converter.ProviderClrType);
        }
    }

    [Fact]
    public void GrainDirectoryModel_HasExpectedKeyAndLookupIndexes()
    {
        using var context = CreateContext(DatabaseProvider.PostgreSql, Feature.GrainDirectory);
        var activation = context.Model.FindEntityType(typeof(GrainActivationRecord<Guid>))!;

        Assert.Equal(["ClusterId", "GrainId"], GetPropertyNames(activation.FindPrimaryKey()!.Properties));
        AssertIndexSet(
            activation,
            ["ClusterId", "SiloAddress"],
            ["ClusterId", "GrainId", "ActivationId"]);
    }

    [Theory]
    [InlineData(DatabaseProvider.MySql, 191)]
    [InlineData(DatabaseProvider.PostgreSql, 280)]
    public void PersistenceModel_HasFourColumnKeyAndProviderSpecificLengths(
        DatabaseProvider provider,
        int expectedMaxLength)
    {
        using var context = CreateContext(provider, Feature.Persistence);
        var state = context.Model.FindEntityType(typeof(GrainStateRecord<Guid>))!;
        var keyNames = new[] { "ServiceId", "GrainType", "StateType", "GrainId" };

        Assert.Equal(keyNames, GetPropertyNames(state.FindPrimaryKey()!.Properties));
        Assert.All(keyNames, name => Assert.Equal(expectedMaxLength, state.FindProperty(name)!.GetMaxLength()));
        Assert.True(state.FindProperty("Data")!.IsNullable);
    }

    [Fact]
    public void ReminderModel_HasExpectedKeyAndLookupIndexes()
    {
        using var context = CreateContext(DatabaseProvider.PostgreSql, Feature.Reminders);
        var reminder = context.Model.FindEntityType(typeof(ReminderRecord<Guid>))!;

        Assert.Equal(["ServiceId", "GrainId", "Name"], GetPropertyNames(reminder.FindPrimaryKey()!.Properties));
        AssertIndexSet(
            reminder,
            ["ServiceId", "GrainHash"],
            ["ServiceId", "GrainId"]);
    }

    [Theory]
    [InlineData(DatabaseProvider.MySql)]
    [InlineData(DatabaseProvider.PostgreSql)]
    public void ReminderPeriod_UsesInt64TicksAndRoundTripsLongDurations(DatabaseProvider provider)
    {
        using var context = CreateContext(provider, Feature.Reminders);
        var property = context.Model.FindEntityType(typeof(ReminderRecord<Guid>))!.FindProperty("Period")!;
        var converter = Assert.IsAssignableFrom<ValueConverter>(property.GetValueConverter());

        Assert.Equal(typeof(long), converter.ProviderClrType);
        foreach (var period in new[] { TimeSpan.FromHours(25), TimeSpan.FromDays(36) })
        {
            var ticks = Assert.IsType<long>(converter.ConvertToProvider(period));
            Assert.Equal(period.Ticks, ticks);
            Assert.Equal(period, Assert.IsType<TimeSpan>(converter.ConvertFromProvider(ticks)));
        }
    }

    [Theory]
    [InlineData(Feature.Clustering, "Id,ClusterId,Address")]
    [InlineData(Feature.GrainDirectory, "ClusterId,GrainId,SiloAddress,ActivationId")]
    [InlineData(Feature.Persistence, "ServiceId,GrainType,StateType,GrainId")]
    [InlineData(Feature.Reminders, "ServiceId,GrainId,Name")]
    public void MySql_IdentifierPropertiesUseBinaryCollation(Feature feature, string propertyNames)
    {
        using var context = CreateContext(DatabaseProvider.MySql, feature);
        var expectedNames = propertyNames.Split(',');
        var properties = context.GetService<IDesignTimeModel>().Model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties())
            .Where(property => expectedNames.Contains(property.Name))
            .ToArray();

        Assert.NotEmpty(properties);
        Assert.All(properties, property => Assert.Equal("utf8mb4_bin", property.GetCollation()));
    }

    private static IProperty[] GetETagProperties(IModel model) =>
        model.GetEntityTypes()
            .Select(entity => entity.FindProperty("ETag"))
            .Where(property => property is not null)
            .Cast<IProperty>()
            .ToArray();

    private static string[] GetPropertyNames(IEnumerable<IProperty> properties) =>
        properties.Select(property => property.Name).ToArray();

    private static void AssertIndexSet(IEntityType entity, params string[][] expectedIndexes)
    {
        var actual = entity.GetIndexes()
            .Select(index => string.Join(",", GetPropertyNames(index.Properties)))
            .OrderBy(value => value)
            .ToArray();
        var expected = expectedIndexes
            .Select(index => string.Join(",", index))
            .OrderBy(value => value)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    private static DbContext CreateContext(DatabaseProvider provider, Feature feature) =>
        (provider, feature) switch
        {
            (DatabaseProvider.MySql, Feature.Clustering) => CreateMySqlContext<MySqlClusterDbContext>(
                options => new MySqlClusterDbContext(options)),
            (DatabaseProvider.MySql, Feature.GrainDirectory) => CreateMySqlContext<MySqlGrainDirectoryDbContext>(
                options => new MySqlGrainDirectoryDbContext(options)),
            (DatabaseProvider.MySql, Feature.Persistence) => CreateMySqlContext<MySqlGrainStateDbContext>(
                options => new MySqlGrainStateDbContext(options)),
            (DatabaseProvider.MySql, Feature.Reminders) => CreateMySqlContext<MySqlReminderDbContext>(
                options => new MySqlReminderDbContext(options)),
            (DatabaseProvider.PostgreSql, Feature.Clustering) => CreatePostgreSqlContext<PostgreSqlClusterDbContext>(
                options => new PostgreSqlClusterDbContext(options)),
            (DatabaseProvider.PostgreSql, Feature.GrainDirectory) => CreatePostgreSqlContext<PostgreSqlGrainDirectoryDbContext>(
                options => new PostgreSqlGrainDirectoryDbContext(options)),
            (DatabaseProvider.PostgreSql, Feature.Persistence) => CreatePostgreSqlContext<PostgreSqlGrainStateDbContext>(
                options => new PostgreSqlGrainStateDbContext(options)),
            (DatabaseProvider.PostgreSql, Feature.Reminders) => CreatePostgreSqlContext<PostgreSqlReminderDbContext>(
                options => new PostgreSqlReminderDbContext(options)),
            _ => throw new ArgumentOutOfRangeException(nameof(feature))
        };

    private static TContext CreateMySqlContext<TContext>(Func<DbContextOptions<TContext>, TContext> factory)
        where TContext : DbContext
    {
        var builder = new DbContextOptionsBuilder<TContext>();
        EFCoreTestDatabase.MySql.ConfigureOptions(
            builder,
            "Server=localhost;Database=metadata;User ID=test;Password=test",
            typeof(TContext).Assembly.GetName().Name!);
        return factory(builder.Options);
    }

    private static TContext CreatePostgreSqlContext<TContext>(Func<DbContextOptions<TContext>, TContext> factory)
        where TContext : DbContext
    {
        var builder = new DbContextOptionsBuilder<TContext>();
        EFCoreTestDatabase.PostgreSql.ConfigureOptions(
            builder,
            "Host=localhost;Database=metadata;Username=test;Password=test",
            typeof(TContext).Assembly.GetName().Name!);
        return factory(builder.Options);
    }

    public enum DatabaseProvider
    {
        MySql,
        PostgreSql
    }

    public enum Feature
    {
        Clustering,
        GrainDirectory,
        Persistence,
        Reminders
    }
}
