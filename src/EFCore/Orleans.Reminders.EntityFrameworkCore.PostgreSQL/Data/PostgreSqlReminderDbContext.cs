using Microsoft.EntityFrameworkCore;
using Orleans.Reminders.EntityFrameworkCore.Data;

namespace Orleans.Reminders.EntityFrameworkCore.PostgreSQL.Data;

public class PostgreSqlReminderDbContext : GuidReminderDbContext<PostgreSqlReminderDbContext>
{
    public PostgreSqlReminderDbContext(DbContextOptions<PostgreSqlReminderDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ReminderRecord<Guid>>(entity =>
        {
            entity.Property(record => record.ServiceIdHash).HasColumnType("bytea");
            entity.Property(record => record.GrainIdHash).HasColumnType("bytea");
            entity.Property(record => record.ReminderNameHash).HasColumnType("bytea");
            entity.ToTable(table =>
            {
                table.HasCheckConstraint("CK_Reminders_ServiceIdHash_Length", "octet_length(\"ServiceIdHash\") = 32");
                table.HasCheckConstraint("CK_Reminders_GrainIdHash_Length", "octet_length(\"GrainIdHash\") = 32");
                table.HasCheckConstraint("CK_Reminders_ReminderNameHash_Length", "octet_length(\"ReminderNameHash\") = 32");
            });
        });
    }
}
