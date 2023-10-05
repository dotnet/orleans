using Microsoft.EntityFrameworkCore;

namespace Orleans.Clustering.EntityFrameworkCore.Data;

public class ClusterDbContext<TDbContext> : DbContext where TDbContext : DbContext
{
    public DbSet<ClusterRecord> Clusters { get; set; } = default!;
    public DbSet<SiloRecord> Silos { get; set; } = default!;

    public ClusterDbContext(DbContextOptions<TDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ClusterRecord>(c =>
        {
            c.HasKey(p => p.Id);
            c.Property(p => p.Timestamp).IsRequired();
            c.Property(p => p.Version).IsRequired();
            c.Property(p => p.ETag).IsRequired().IsRowVersion();

            c
                .HasMany(p => p.Silos)
                .WithOne(r => r.Cluster)
                .HasForeignKey(r => r.ClusterId);
        });

        modelBuilder.Entity<SiloRecord>(c =>
        {
            c.HasKey(p => new {p.ClusterId, p.Address, p.Port, p.Generation});
            c.Property(p => p.Address).HasMaxLength(45).IsRequired();
            c.Property(p => p.Port).IsRequired();
            c.Property(p => p.Generation).IsRequired();
            c.Property(p => p.Name).HasMaxLength(150).IsRequired();
            c.Property(p => p.HostName).HasMaxLength(150).IsRequired();
            c.Property(p => p.Status).IsRequired();
            c.Property(p => p.ProxyPort).IsRequired(false);
            c.Property(p => p.SuspectingTimes).IsRequired(false);
            c.Property(p => p.SuspectingSilos).IsRequired(false);
            c.Property(p => p.StartTime).IsRequired();
            c.Property(p => p.IAmAliveTime).IsRequired();
            c.Property(p => p.ETag).IsRequired().IsRowVersion();

            c
                .HasOne(p => p.Cluster)
                .WithMany(p => p.Silos)
                .HasForeignKey(p => p.ClusterId);
        });
    }
}