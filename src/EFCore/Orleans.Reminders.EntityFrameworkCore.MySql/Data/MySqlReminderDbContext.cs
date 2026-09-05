using Microsoft.EntityFrameworkCore;
using Orleans.Reminders.EntityFrameworkCore.Data;

namespace Orleans.Reminders.EntityFrameworkCore.MySql.Data;

public class MySqlReminderDbContext : GuidReminderDbContext<MySqlReminderDbContext>
{
    private const string IdentifierCollation = "utf8mb4_bin";

    public MySqlReminderDbContext(DbContextOptions<MySqlReminderDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ReminderRecord<Guid>>(entity =>
        {
            entity.Property(record => record.ServiceId).UseCollation(IdentifierCollation);
            entity.Property(record => record.GrainId).UseCollation(IdentifierCollation);
            entity.Property(record => record.Name).UseCollation(IdentifierCollation);
        });
    }
}
