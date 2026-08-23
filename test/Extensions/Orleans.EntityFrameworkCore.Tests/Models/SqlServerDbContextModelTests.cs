using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Orleans.Clustering.EntityFrameworkCore.Data;
using Orleans.Clustering.EntityFrameworkCore.SqlServer.Data;
using Orleans.EntityFrameworkCore.Tests.Infrastructure;
using Orleans.GrainDirectory.EntityFrameworkCore.Data;
using Orleans.GrainDirectory.EntityFrameworkCore.SqlServer.Data;
using Orleans.Persistence.EntityFrameworkCore.Data;
using Orleans.Persistence.EntityFrameworkCore.SqlServer.Data;
using Orleans.Reminders.EntityFrameworkCore.Data;
using Orleans.Reminders.EntityFrameworkCore.SqlServer.Data;
using TestExtensions;

namespace Orleans.EntityFrameworkCore.Tests.Models;

[TestArea("EFCore")]
[TestProvider("None")]
[TestSuite("BVT")]
public sealed class SqlServerDbContextModelTests
{
    [Theory]
    [InlineData(Feature.Clustering, 2)]
    [InlineData(Feature.GrainDirectory, 1)]
    [InlineData(Feature.Persistence, 1)]
    [InlineData(Feature.Reminders, 1)]
    public void ETag_IsRequiredDatabaseGeneratedRowVersion(Feature feature, int expectedCount)
    {
        using var context = CreateContext(feature);
        var etags = context.Model.GetEntityTypes()
            .Select(entity => entity.FindProperty("ETag"))
            .Where(property => property is not null)
            .Cast<IProperty>()
            .ToArray();

        Assert.Equal(expectedCount, etags.Length);
        Assert.All(etags, property =>
        {
            Assert.Equal(typeof(byte[]), property.ClrType);
            Assert.False(property.IsNullable);
            Assert.True(property.IsConcurrencyToken);
            Assert.Equal(ValueGenerated.OnAddOrUpdate, property.ValueGenerated);
            Assert.Equal(PropertySaveBehavior.Ignore, property.GetBeforeSaveBehavior());
            Assert.Equal(PropertySaveBehavior.Ignore, property.GetAfterSaveBehavior());
            Assert.Equal("rowversion", property.GetColumnType());
        });
    }

    [Fact]
    public void ClusteringModel_RetainsKeysAndLookupIndexes()
    {
        using var context = CreateContext(Feature.Clustering);
        var cluster = context.Model.FindEntityType(typeof(ClusterRecord<byte[]>))!;
        var silo = context.Model.FindEntityType(typeof(SiloRecord<byte[]>))!;

        Assert.Equal(["Id"], GetPropertyNames(cluster.FindPrimaryKey()!.Properties));
        Assert.Equal(
            ["ClusterId", "Address", "Port", "Generation"],
            GetPropertyNames(silo.FindPrimaryKey()!.Properties));
        AssertIndexSet(
            silo,
            ["ClusterId"],
            ["ClusterId", "Status"],
            ["ClusterId", "Status", "IAmAliveTime"]);
    }

    [Fact]
    public void ClusteringModel_SuspectListConverterAndComparer_HandleNullValues()
    {
        using var context = CreateContext(Feature.Clustering);
        var silo = context.Model.FindEntityType(typeof(SiloRecord<byte[]>))!;

        foreach (var propertyName in new[] { "SuspectingTimes", "SuspectingSilos" })
        {
            var property = silo.FindProperty(propertyName)!;
            var converter = Assert.IsAssignableFrom<Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter>(
                property.GetValueConverter());
            var comparer = Assert.IsAssignableFrom<Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer>(
                property.GetValueComparer());

            Assert.Null(converter.ConvertToProvider(null));
            Assert.Null(converter.ConvertFromProvider(null));
            Assert.True(comparer.Equals(null, null));
            Assert.Equal(0, comparer.GetHashCode(null));
            Assert.Null(comparer.Snapshot(null));
            Assert.Empty(Assert.IsType<List<string>>(
                converter.ConvertFromProviderExpression.Compile().DynamicInvoke(new object?[] { null })));
            Assert.Empty(Assert.IsType<List<string>>(
                comparer.SnapshotExpression.Compile().DynamicInvoke(new object?[] { null })));
        }
    }

    [Fact]
    public void GrainDirectoryModel_RetainsKeyAndLookupIndexes()
    {
        using var context = CreateContext(Feature.GrainDirectory);
        var activation = context.Model.FindEntityType(typeof(GrainActivationRecord<byte[]>))!;

        Assert.Equal(["ClusterId", "GrainId"], GetPropertyNames(activation.FindPrimaryKey()!.Properties));
        AssertIndexSet(
            activation,
            ["ClusterId", "SiloAddress"],
            ["ClusterId", "GrainId", "ActivationId"]);
        Assert.Equal(150, activation.FindProperty("ClusterId")!.GetMaxLength());
        Assert.Equal(512, activation.FindProperty("GrainId")!.GetMaxLength());
        Assert.Equal(256, activation.FindProperty("SiloAddress")!.GetMaxLength());
        Assert.Equal(64, activation.FindProperty("ActivationId")!.GetMaxLength());
        AssertKeyWithinNonclusteredLimit(activation);
        Assert.Contains("PRIMARY KEY NONCLUSTERED", context.Database.GenerateCreateScript());
    }

    [Fact]
    public void PersistenceModel_RetainsFourColumnKeyAndLengths()
    {
        using var context = CreateContext(Feature.Persistence);
        var state = context.Model.FindEntityType(typeof(GrainStateRecord<byte[]>))!;
        var keyNames = new[] { "ServiceId", "GrainType", "StateType", "GrainId" };

        Assert.Equal(keyNames, GetPropertyNames(state.FindPrimaryKey()!.Properties));
        Assert.Equal(150, state.FindProperty("ServiceId")!.GetMaxLength());
        Assert.Equal(250, state.FindProperty("GrainType")!.GetMaxLength());
        Assert.Equal(150, state.FindProperty("StateType")!.GetMaxLength());
        Assert.Equal(299, state.FindProperty("GrainId")!.GetMaxLength());
        Assert.Equal(typeof(byte[]), state.FindProperty("Data")!.ClrType);
        Assert.True(state.FindProperty("Data")!.IsNullable);
        AssertKeyWithinNonclusteredLimit(state);
        Assert.Contains("PRIMARY KEY NONCLUSTERED", context.Database.GenerateCreateScript());
    }

    [Fact]
    public void ReminderModel_RetainsKeyAndLookupIndexes()
    {
        using var context = CreateContext(Feature.Reminders);
        var reminder = context.Model.FindEntityType(typeof(ReminderRecord<byte[]>))!;

        Assert.Equal(["ServiceId", "GrainId", "Name"], GetPropertyNames(reminder.FindPrimaryKey()!.Properties));
        AssertIndexSet(
            reminder,
            ["ServiceId", "GrainHash"],
            ["ServiceId", "GrainId"]);
        Assert.Equal(150, reminder.FindProperty("ServiceId")!.GetMaxLength());
        Assert.Equal(512, reminder.FindProperty("GrainId")!.GetMaxLength());
        Assert.Equal(150, reminder.FindProperty("Name")!.GetMaxLength());
        AssertKeyWithinNonclusteredLimit(reminder);
        Assert.Contains("PRIMARY KEY NONCLUSTERED", context.Database.GenerateCreateScript());
    }

    [Fact]
    public void ReminderPeriod_UsesInt64TicksAndRoundTripsLongDurations()
    {
        using var context = CreateContext(Feature.Reminders);
        var property = context.Model.FindEntityType(typeof(ReminderRecord<byte[]>))!.FindProperty("Period")!;
        var converter = Assert.IsAssignableFrom<Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter>(
            property.GetValueConverter());

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
    public void IdentifierPropertiesUseBinaryCollation(Feature feature, string propertyNames)
    {
        using var context = CreateContext(feature);
        var expectedNames = propertyNames.Split(',');
        var properties = context.GetService<IDesignTimeModel>().Model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties())
            .Where(property => expectedNames.Contains(property.Name))
            .ToArray();

        Assert.NotEmpty(properties);
        Assert.All(properties, property => Assert.Equal("Latin1_General_100_BIN2", property.GetCollation()));
    }

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

    private static void AssertKeyWithinNonclusteredLimit(IEntityType entity)
    {
        var key = entity.FindPrimaryKey()!;
        Assert.InRange(
            key.Properties.Sum(property => property.GetMaxLength()!.Value * sizeof(char)),
            1,
            1_700);
    }

    private static DbContext CreateContext(Feature feature) => feature switch
    {
        Feature.Clustering => CreateContext<SqlServerClusterDbContext>(
            options => new SqlServerClusterDbContext(options)),
        Feature.GrainDirectory => CreateContext<SqlServerGrainDirectoryDbContext>(
            options => new SqlServerGrainDirectoryDbContext(options)),
        Feature.Persistence => CreateContext<SqlServerGrainStateDbContext>(
            options => new SqlServerGrainStateDbContext(options)),
        Feature.Reminders => CreateContext<SqlServerReminderDbContext>(
            options => new SqlServerReminderDbContext(options)),
        _ => throw new ArgumentOutOfRangeException(nameof(feature))
    };

    private static TContext CreateContext<TContext>(Func<DbContextOptions<TContext>, TContext> factory)
        where TContext : DbContext
    {
        var builder = new DbContextOptionsBuilder<TContext>();
        EFCoreTestDatabase.SqlServer.ConfigureOptions(
            builder,
            "Server=localhost;Database=metadata;User ID=test;Password=not-used;TrustServerCertificate=True",
            typeof(TContext).Assembly.GetName().Name!);
        return factory(builder.Options);
    }

    public enum Feature
    {
        Clustering,
        GrainDirectory,
        Persistence,
        Reminders
    }
}
