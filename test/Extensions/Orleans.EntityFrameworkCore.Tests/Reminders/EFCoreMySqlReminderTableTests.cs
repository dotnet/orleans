using Orleans.EntityFrameworkCore.Tests.Infrastructure;
using Orleans.Reminders.EntityFrameworkCore.MySql.Data;
using TestExtensions;
using UnitTests;
using Xunit.Abstractions;

namespace Orleans.EntityFrameworkCore.Tests.Reminders;

[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestCategory("Reminders")]
[TestCategory("EFCore")]
[TestCategory(EFCoreTestCategories.MySql)]
[TestCategory(EFCoreTestCategories.MySqlProvider)]
[TestSuite("Functional")]
[TestProvider(EFCoreTestCategories.MySqlProvider)]
[TestArea("Reminders")]
public sealed class EFCoreMySqlReminderTableTests :
    EFCoreReminderTableTestsBase<MySqlReminderDbContext, Guid>
{
    public EFCoreMySqlReminderTableTests(
        ConnectionStringFixture fixture,
        TestEnvironmentFixture environment,
        ITestOutputHelper testOutput)
        : base(fixture, environment, testOutput)
    {
    }

    protected override EFCoreTestDatabase Database => EFCoreTestDatabase.MySql;

    protected override IEFReminderETagConverter<Guid> CreateETagConverter() =>
        new GuidReminderETagConverter();
}
