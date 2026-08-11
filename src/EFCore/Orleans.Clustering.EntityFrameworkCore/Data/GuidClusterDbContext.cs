using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Orleans.Clustering.EntityFrameworkCore.Data;

public abstract class GuidClusterDbContext<TDbContext> : ClusterDbContext<TDbContext, Guid>
    where TDbContext : DbContext
{
    protected GuidClusterDbContext(DbContextOptions<TDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var listConverter = new ValueConverter<List<string>, string>(
            value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
            value => JsonSerializer.Deserialize<List<string>>(value, (JsonSerializerOptions?)null) ?? new List<string>());
        var listComparer = new ValueComparer<List<string>>(
            (left, right) => ReferenceEquals(left, right) || left != null && right != null && left.SequenceEqual(right),
            value => value.Aggregate(0, (hash, item) => HashCode.Combine(hash, item)),
            value => value.ToList());

        modelBuilder.Entity<ClusterRecord<Guid>>(entity =>
        {
            entity.HasKey(record => record.Id);
            entity.Property(record => record.Timestamp).IsRequired();
            entity.Property(record => record.Version).IsRequired();
            entity.Property(record => record.ETag)
                .IsRequired()
                .ValueGeneratedNever()
                .IsConcurrencyToken();
            entity.HasMany(record => record.Silos)
                .WithOne(record => record.Cluster)
                .HasForeignKey(record => record.ClusterId);
        });

        modelBuilder.Entity<SiloRecord<Guid>>(entity =>
        {
            entity.HasKey(record => new { record.ClusterId, record.Address, record.Port, record.Generation });
            entity.Property(record => record.Address).HasMaxLength(45).IsRequired();
            entity.Property(record => record.Port).IsRequired();
            entity.Property(record => record.Generation).IsRequired();
            entity.Property(record => record.Name).HasMaxLength(150).IsRequired();
            entity.Property(record => record.HostName).HasMaxLength(150).IsRequired();
            entity.Property(record => record.Status).IsRequired();
            entity.Property(record => record.ProxyPort).IsRequired(false);
            entity.Property(record => record.SuspectingTimes)
                .IsRequired(false)
                .HasConversion(listConverter)
                .Metadata.SetValueComparer(listComparer);
            entity.Property(record => record.SuspectingSilos)
                .IsRequired(false)
                .HasConversion(listConverter)
                .Metadata.SetValueComparer(listComparer);
            entity.Property(record => record.StartTime).IsRequired();
            entity.Property(record => record.IAmAliveTime).IsRequired();
            entity.Property(record => record.ETag)
                .IsRequired()
                .ValueGeneratedNever()
                .IsConcurrencyToken();
            entity.HasOne(record => record.Cluster)
                .WithMany(record => record.Silos)
                .HasForeignKey(record => record.ClusterId);
            entity.HasIndex(record => record.ClusterId);
            entity.HasIndex(record => new { record.ClusterId, record.Status });
            entity.HasIndex(record => new { record.ClusterId, record.Status, record.IAmAliveTime });
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
        foreach (var entry in ChangeTracker.Entries().Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            var property = entry.Metadata.FindProperty(nameof(ClusterRecord<Guid>.ETag));
            if (property?.ClrType == typeof(Guid))
            {
                entry.Property(property).CurrentValue = Guid.NewGuid();
            }
        }
    }
}
