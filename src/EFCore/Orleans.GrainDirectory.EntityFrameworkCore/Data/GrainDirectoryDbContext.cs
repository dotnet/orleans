using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Orleans.GrainDirectory.EntityFrameworkCore.Data;

public class GrainDirectoryDbContext<TDbContext, TETag> : DbContext where TDbContext : DbContext
{
    public DbSet<GrainActivationRecord<TETag>> Activations { get; set; } = default!;

    public GrainDirectoryDbContext(DbContextOptions<TDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GrainActivationRecord<TETag>>(c =>
        {
            c.HasKey(p => new { p.ClusterIdHash, p.GrainIdHash });
            c.Property(p => p.ClusterIdHash).HasMaxLength(32).IsRequired().UsePropertyAccessMode(PropertyAccessMode.Property);
            c.Property(p => p.GrainIdHash).HasMaxLength(32).IsRequired().UsePropertyAccessMode(PropertyAccessMode.Property);
            c.Property(p => p.SiloAddressHash).HasMaxLength(32).IsRequired().UsePropertyAccessMode(PropertyAccessMode.Property);
            c.Property(p => p.ClusterId).IsRequired();
            c.Property(p => p.GrainId).IsRequired();
            c.Property(p => p.SiloAddress).IsRequired();
            c.Property(p => p.ActivationId).IsRequired();
            c.Property(p => p.MembershipVersion).IsRequired();
            c.Property(p => p.ETag).IsRequired().IsConcurrencyToken();
            c.HasIndex(p => new { p.ClusterIdHash, p.SiloAddressHash });
        });
    }
}
