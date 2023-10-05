using Microsoft.EntityFrameworkCore;

namespace Orleans.GrainDirectory.EntityFrameworkCore.Data;

public class GrainDirectoryDbContext<TDbContext> : DbContext where TDbContext : DbContext
{
    public DbSet<GrainActivationRecord> Activations { get; set; } = default!;

    public GrainDirectoryDbContext(DbContextOptions<TDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GrainActivationRecord>(c =>
        {
            c.HasKey(p => new {p.ClusterId, p.GrainId});
            c.Property(p => p.ClusterId).IsRequired();
            c.Property(p => p.GrainId).IsRequired();
            c.Property(p => p.SiloAddress).IsRequired();
            c.Property(p => p.ActivationId).IsRequired();
            c.Property(p => p.MembershipVersion).IsRequired();
            c.Property(p => p.ETag).IsRequired().IsRowVersion();
        });
    }
}