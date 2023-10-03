using Microsoft.EntityFrameworkCore;

namespace Orleans.Persistence.EntityFrameworkCore.Data;

public class GrainStateDbContext : DbContext
{
    public DbSet<GrainStateRecord> GrainState { get; set; } = default!;

    public GrainStateDbContext(DbContextOptions<GrainStateDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GrainStateRecord>(c =>
        {
            c.HasKey(p => new {p.ServiceId, p.GrainType, p.StateType, p.GrainId});
            c.Property(p => p.ServiceId).HasMaxLength(280).IsRequired();
            c.Property(p => p.GrainType).HasMaxLength(280).IsRequired();
            c.Property(p => p.StateType).HasMaxLength(280).IsRequired();
            c.Property(p => p.GrainId).HasMaxLength(280).IsRequired();
            c.Property(p => p.Data).IsRequired(false);
            c.Property(p => p.ETag).IsRequired().IsRowVersion();
        });
    }
}
