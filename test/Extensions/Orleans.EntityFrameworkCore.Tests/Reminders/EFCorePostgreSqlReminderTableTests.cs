using Orleans.EntityFrameworkCore.Tests.Infrastructure;
using Orleans.Reminders.EntityFrameworkCore.PostgreSQL.Data;
using TestExtensions;
using UnitTests;

namespace Orleans.EntityFrameworkCore.Tests.Reminders;

[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestArea("EFCore")]
[TestSuite("Functional")]
[TestProvider(EFCoreTestCategories.PostgreSqlProvider)]
[TestArea("Reminders")]
public sealed class EFCorePostgreSqlReminderTableTests :
    EFCoreReminderTableTestsBase<PostgreSqlReminderDbContext, Guid>
{
    public EFCorePostgreSqlReminderTableTests(
        ConnectionStringFixture fixture,
        TestEnvironmentFixture environment,
        ITestOutputHelper testOutput)
        : base(fixture, environment, testOutput)
    {
    }

    protected override EFCoreTestDatabase Database => EFCoreTestDatabase.PostgreSql;

    protected override IEFReminderETagConverter<Guid> CreateETagConverter() =>
        new GuidReminderETagConverter();
}
