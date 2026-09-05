using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Orleans.Persistence.EntityFrameworkCore.Data;

public abstract class GuidGrainStateDbContext<TDbContext> : GrainStateDbContext<TDbContext, Guid>
    where TDbContext : DbContext
{
    protected GuidGrainStateDbContext(DbContextOptions<TDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GrainStateRecord<Guid>>(entity =>
        {
            entity.HasKey(record => new { record.ServiceId, record.GrainType, record.StateType, record.GrainId });
            entity.Property(record => record.ServiceId).HasMaxLength(280).IsRequired();
            entity.Property(record => record.GrainType).HasMaxLength(280).IsRequired();
            entity.Property(record => record.StateType).HasMaxLength(280).IsRequired();
            entity.Property(record => record.GrainId).HasMaxLength(280).IsRequired();
            entity.Property(record => record.Data).IsRequired(false);
            entity.Property(record => record.ETag)
                .IsRequired()
                .ValueGeneratedNever()
                .IsConcurrencyToken();
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
        foreach (var entry in ChangeTracker.Entries<GrainStateRecord<Guid>>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            entry.Property(record => record.ETag).CurrentValue = Guid.NewGuid();
        }
    }
}
