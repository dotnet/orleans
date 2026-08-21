using Orleans.EntityFrameworkCore.Tests.Infrastructure;
using Orleans.Reminders.EntityFrameworkCore.SqlServer.Data;
using TestExtensions;
using UnitTests;

namespace Orleans.EntityFrameworkCore.Tests.Reminders;

[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestArea("EFCore")]
[TestSuite("Functional")]
[TestProvider(EFCoreTestCategories.SqlServer)]
[TestArea("Reminders")]
public sealed class EFCoreSqlServerReminderTableTests :
    EFCoreReminderTableTestsBase<SqlServerReminderDbContext, byte[]>
{
    public EFCoreSqlServerReminderTableTests(
        ConnectionStringFixture fixture,
        TestEnvironmentFixture environment,
        ITestOutputHelper testOutput)
        : base(fixture, environment, testOutput)
    {
    }

    protected override EFCoreTestDatabase Database => EFCoreTestDatabase.SqlServer;

    protected override IEFReminderETagConverter<byte[]> CreateETagConverter() =>
        new Orleans.Reminders.SqlServerReminderETagConverter();
}
