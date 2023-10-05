using Microsoft.EntityFrameworkCore;
using Orleans.Reminders.EntityFrameworkCore.Data;

namespace Orleans.Reminders.EntityFrameworkCore.SqlServer.Data;

public class SqlServerReminderDbContext : ReminderDbContext<SqlServerReminderDbContext>
{
    public SqlServerReminderDbContext(DbContextOptions<SqlServerReminderDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ReminderRecord>(c =>
        {
            c.HasKey(p => new {p.ServiceId, p.GrainId, p.Name}).HasName("PK_Reminders");
            c.Property(p => p.ServiceId).IsRequired();
            c.Property(p => p.GrainId).IsRequired();
            c.Property(p => p.Name).IsRequired();
            c.Property(p => p.StartAt).IsRequired();
            c.Property(p => p.Period).IsRequired();
            c.Property(p => p.GrainHash).IsRequired();
            c.Property(p => p.ETag).IsRequired().IsRowVersion();

            c.HasIndex(p => new {p.ServiceId, p.GrainHash}).IsClustered(false).HasDatabaseName("IDX_Reminders_ServiceId_GrainHash");
            c.HasIndex(p => new {p.ServiceId, p.GrainId}).IsClustered(false).HasDatabaseName("IDX_Reminders_ServiceId_GrainId");
        });
    }
}