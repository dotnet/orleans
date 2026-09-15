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
            entity.Property(record => record.ServiceIdHash).HasColumnType("binary(32)");
            entity.Property(record => record.GrainIdHash).HasColumnType("binary(32)");
            entity.Property(record => record.ReminderNameHash).HasColumnType("binary(32)");
            entity.Property(record => record.ServiceId).HasColumnType("longtext").UseCollation(IdentifierCollation);
            entity.Property(record => record.GrainId).HasColumnType("longtext").UseCollation(IdentifierCollation);
            entity.Property(record => record.Name).HasColumnType("longtext").UseCollation(IdentifierCollation);
        });
    }
}
