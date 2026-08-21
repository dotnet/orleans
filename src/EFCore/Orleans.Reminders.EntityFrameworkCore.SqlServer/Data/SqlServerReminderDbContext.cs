using Microsoft.EntityFrameworkCore;
using Orleans.Reminders.EntityFrameworkCore.Data;

namespace Orleans.Reminders.EntityFrameworkCore.SqlServer.Data;

public class SqlServerReminderDbContext : ReminderDbContext<SqlServerReminderDbContext, byte[]>
{
    private const string IdentifierCollation = "Latin1_General_100_BIN2";

    public SqlServerReminderDbContext(DbContextOptions<SqlServerReminderDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ReminderRecord<byte[]>>(c =>
        {
            c.HasKey(p => new {p.ServiceId, p.GrainId, p.Name}).IsClustered(false).HasName("PK_Reminders");
            c.Property(p => p.ServiceId).HasMaxLength(150).UseCollation(IdentifierCollation).IsRequired();
            c.Property(p => p.GrainId).HasMaxLength(512).UseCollation(IdentifierCollation).IsRequired();
            c.Property(p => p.Name).HasMaxLength(150).UseCollation(IdentifierCollation).IsRequired();
            c.Property(p => p.StartAt).IsRequired();
            c.Property(p => p.Period)
                .HasConversion(period => period.Ticks, ticks => TimeSpan.FromTicks(ticks))
                .IsRequired();
            c.Property(p => p.GrainHash).IsRequired();
            c.Property(p => p.ETag).IsRequired().IsRowVersion();

            c.HasIndex(p => new {p.ServiceId, p.GrainHash}).IsClustered(false).HasDatabaseName("IDX_Reminders_ServiceId_GrainHash");
            c.HasIndex(p => new {p.ServiceId, p.GrainId}).IsClustered(false).HasDatabaseName("IDX_Reminders_ServiceId_GrainId");
        });
    }
}