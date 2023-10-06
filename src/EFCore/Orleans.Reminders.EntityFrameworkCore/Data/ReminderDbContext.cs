using Microsoft.EntityFrameworkCore;

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
            c.HasKey(p => new {p.ServiceId, p.GrainId, p.Name});
            c.Property(p => p.ServiceId).IsRequired();
            c.Property(p => p.GrainId).IsRequired();
            c.Property(p => p.Name).IsRequired();
            c.Property(p => p.StartAt).IsRequired();
            c.Property(p => p.Period).IsRequired();
            c.Property(p => p.GrainHash).IsRequired();
            c.Property(p => p.ETag).IsRequired().IsRowVersion();
        });
    }
}