using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Orleans.Reminders.EntityFrameworkCore.Data;

public class ReminderDbContext<TDbContext, TETag> : DbContext where TDbContext : DbContext
{
    public DbSet<ReminderRecord<TETag>> Reminders { get; set; } = default!;

    public ReminderDbContext(DbContextOptions<TDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ReminderRecord<TETag>>(c =>
        {
            c.HasKey(p => new {p.ServiceIdHash, p.GrainIdHash, p.ReminderNameHash});
            c.Property(p => p.ServiceIdHash).HasMaxLength(32).IsRequired().UsePropertyAccessMode(PropertyAccessMode.Property);
            c.Property(p => p.GrainIdHash).HasMaxLength(32).IsRequired().UsePropertyAccessMode(PropertyAccessMode.Property);
            c.Property(p => p.ReminderNameHash).HasMaxLength(32).IsRequired().UsePropertyAccessMode(PropertyAccessMode.Property);
            c.Property(p => p.ServiceId).IsRequired();
            c.Property(p => p.GrainId).IsRequired();
            c.Property(p => p.Name).IsRequired();
            c.Property(p => p.StartAt).IsRequired();
            c.Property(p => p.Period)
                .HasConversion(period => period.Ticks, ticks => TimeSpan.FromTicks(ticks))
                .IsRequired();
            c.Property(p => p.GrainHash).IsRequired();
            c.Property(p => p.ETag).IsRequired().IsConcurrencyToken();
            c.HasIndex(p => new {p.ServiceIdHash, p.GrainHash});
            c.HasIndex(p => new {p.ServiceIdHash, p.GrainIdHash});
        });
    }
}