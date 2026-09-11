using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Orleans.Persistence.EntityFrameworkCore.Data;

public class GrainStateDbContext<TDbContext, TETag> : DbContext where TDbContext : DbContext
{
    public DbSet<GrainStateRecord<TETag>> GrainState { get; set; } = default!;

    public GrainStateDbContext(DbContextOptions<TDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GrainStateRecord<TETag>>(c =>
        {
            c.HasKey(p => p.KeyHash);
            c.Property(p => p.KeyHash)
                .HasMaxLength(32)
                .IsRequired()
                .UsePropertyAccessMode(PropertyAccessMode.Property);
            c.Property(p => p.ServiceId).IsRequired();
            c.Property(p => p.GrainType).IsRequired();
            c.Property(p => p.StateType).IsRequired();
            c.Property(p => p.GrainId).IsRequired();
            c.Property(p => p.Data).IsRequired(false);
            c.Property(p => p.ETag).IsRequired().IsConcurrencyToken();
        });
    }
}
