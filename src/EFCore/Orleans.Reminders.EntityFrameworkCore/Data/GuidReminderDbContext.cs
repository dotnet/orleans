using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Orleans.Reminders.EntityFrameworkCore.Data;

public abstract class GuidReminderDbContext<TDbContext> : ReminderDbContext<TDbContext, Guid>
    where TDbContext : DbContext
{
    protected GuidReminderDbContext(DbContextOptions<TDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ReminderRecord<Guid>>(entity =>
        {
            entity.HasKey(record => new { record.ServiceIdHash, record.GrainIdHash, record.ReminderNameHash });
            entity.Property(record => record.ServiceIdHash).HasMaxLength(32).IsRequired().UsePropertyAccessMode(PropertyAccessMode.Property);
            entity.Property(record => record.GrainIdHash).HasMaxLength(32).IsRequired().UsePropertyAccessMode(PropertyAccessMode.Property);
            entity.Property(record => record.ReminderNameHash).HasMaxLength(32).IsRequired().UsePropertyAccessMode(PropertyAccessMode.Property);
            entity.Property(record => record.ServiceId).IsRequired();
            entity.Property(record => record.GrainId).IsRequired();
            entity.Property(record => record.Name).IsRequired();
            entity.Property(record => record.StartAt).IsRequired();
            entity.Property(record => record.Period)
                .HasConversion(period => period.Ticks, ticks => TimeSpan.FromTicks(ticks))
                .IsRequired();
            entity.Property(record => record.GrainHash).IsRequired();
            entity.Property(record => record.ETag)
                .IsRequired()
                .ValueGeneratedNever()
                .IsConcurrencyToken();
            entity.HasIndex(record => new { record.ServiceIdHash, record.GrainHash });
            entity.HasIndex(record => new { record.ServiceIdHash, record.GrainIdHash });
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        UpdateETags();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        UpdateETags();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void UpdateETags()
    {
        foreach (var entry in ChangeTracker.Entries<ReminderRecord<Guid>>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            entry.Property(record => record.ETag).CurrentValue = Guid.NewGuid();
        }
    }
}
