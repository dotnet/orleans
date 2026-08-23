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
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ReminderRecord<byte[]>>(c =>
        {
            c.HasKey(p => new {p.ServiceIdHash, p.GrainIdHash, p.ReminderNameHash}).IsClustered(false).HasName("PK_Reminders");
            c.Property(p => p.ServiceIdHash).HasColumnType("binary(32)");
            c.Property(p => p.GrainIdHash).HasColumnType("binary(32)");
            c.Property(p => p.ReminderNameHash).HasColumnType("binary(32)");
            c.Property(p => p.ServiceId).HasColumnType("nvarchar(max)").UseCollation(IdentifierCollation);
            c.Property(p => p.GrainId).HasColumnType("nvarchar(max)").UseCollation(IdentifierCollation);
            c.Property(p => p.Name).HasColumnType("nvarchar(max)").UseCollation(IdentifierCollation);
            c.Property(p => p.ETag).IsRequired().IsRowVersion();

            c.HasIndex(p => new {p.ServiceIdHash, p.GrainHash}).IsClustered(false).HasDatabaseName("IDX_Reminders_ServiceIdHash_GrainHash");
            c.HasIndex(p => new {p.ServiceIdHash, p.GrainIdHash}).IsClustered(false).HasDatabaseName("IDX_Reminders_ServiceIdHash_GrainIdHash");
        });
    }
}