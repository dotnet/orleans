using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Orleans.GrainDirectory.EntityFrameworkCore.Data;

public abstract class GuidGrainDirectoryDbContext<TDbContext> : GrainDirectoryDbContext<TDbContext, Guid>
    where TDbContext : DbContext
{
    protected GuidGrainDirectoryDbContext(DbContextOptions<TDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GrainActivationRecord<Guid>>(entity =>
        {
            entity.HasKey(record => new { record.ClusterId, record.GrainId });
            entity.Property(record => record.ClusterId).IsRequired();
            entity.Property(record => record.GrainId).IsRequired();
            entity.Property(record => record.SiloAddress).IsRequired();
            entity.Property(record => record.ActivationId).IsRequired();
            entity.Property(record => record.MembershipVersion).IsRequired();
            entity.Property(record => record.ETag)
                .IsRequired()
                .ValueGeneratedNever()
                .IsConcurrencyToken();
            entity.HasIndex(record => new { record.ClusterId, record.SiloAddress });
            entity.HasIndex(record => new { record.ClusterId, record.GrainId, record.ActivationId });
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
        foreach (var entry in ChangeTracker.Entries<GrainActivationRecord<Guid>>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            entry.Property(record => record.ETag).CurrentValue = Guid.NewGuid();
        }
    }
}
