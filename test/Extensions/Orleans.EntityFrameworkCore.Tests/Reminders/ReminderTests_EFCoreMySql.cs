using Orleans.EntityFrameworkCore.Tests.Infrastructure;
using Orleans.Reminders.EntityFrameworkCore.MySql.Data;
using TestExtensions;

namespace Orleans.EntityFrameworkCore.Tests.Reminders;

[TestCategory("Reminders")]
[TestCategory("EFCore")]
[TestCategory(EFCoreTestCategories.MySql)]
[TestCategory(EFCoreTestCategories.MySqlProvider)]
[TestSuite("Functional")]
[TestProvider(EFCoreTestCategories.MySqlProvider)]
[TestArea("Reminders")]
public sealed class ReminderTests_EFCoreMySql :
    EFCoreReminderServiceTestsBase<
        MySqlReminderDbContext,
        Guid,
        MySqlEFCoreProviderConfiguration>
{
    public ReminderTests_EFCoreMySql(
        EFCoreReminderServiceFixture<
            MySqlReminderDbContext,
            Guid,
            MySqlEFCoreProviderConfiguration> fixture)
        : base(fixture)
    {
    }
}
