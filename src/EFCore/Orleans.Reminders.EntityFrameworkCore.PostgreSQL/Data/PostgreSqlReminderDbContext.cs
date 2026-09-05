using Microsoft.EntityFrameworkCore;
using Orleans.Reminders.EntityFrameworkCore.Data;

namespace Orleans.Reminders.EntityFrameworkCore.PostgreSQL.Data;

public class PostgreSqlReminderDbContext : GuidReminderDbContext<PostgreSqlReminderDbContext>
{
    public PostgreSqlReminderDbContext(DbContextOptions<PostgreSqlReminderDbContext> options) : base(options)
    {
    }
}
