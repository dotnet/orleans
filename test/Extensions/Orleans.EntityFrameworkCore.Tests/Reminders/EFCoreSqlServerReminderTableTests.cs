using Orleans.EntityFrameworkCore.Tests.Infrastructure;
using Orleans.Reminders.EntityFrameworkCore.SqlServer.Data;
using TestExtensions;
using UnitTests;
using Xunit.Abstractions;

namespace Orleans.EntityFrameworkCore.Tests.Reminders;

[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestCategory("Reminders")]
[TestCategory("EFCore")]
[TestCategory(EFCoreTestCategories.SqlServer)]
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
