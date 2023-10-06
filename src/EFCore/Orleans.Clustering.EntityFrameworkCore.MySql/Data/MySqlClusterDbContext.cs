using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Orleans.Clustering.EntityFrameworkCore.Data;

namespace Orleans.Clustering.EntityFrameworkCore.MySql.Data;

public class MySqlClusterDbContext : ClusterDbContext<MySqlClusterDbContext, DateTime>
{
    public MySqlClusterDbContext(DbContextOptions<MySqlClusterDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ClusterRecord<DateTime>>(c =>
        {
            c.HasKey(p => p.Id).HasName("PK_Cluster");
            c.Property(p => p.Timestamp).IsRequired();
            c.Property(p => p.Version).IsRequired();
            c.Property(p => p.ETag).IsRowVersion().IsConcurrencyToken();

            c
                .HasMany(p => p.Silos)
                .WithOne(r => r.Cluster)
                .HasForeignKey(r => r.ClusterId);
        });

        var listToStringConverter = new ValueConverter<List<string>, string>(
            v => string.Join(",", v),
            v => v.Split(new[] {','}, StringSplitOptions.RemoveEmptyEntries).ToList());


        var listComparer = new ValueComparer<List<string>>(
            (c1, c2) => (c1 == null && c2 == null) || (c1 != null && c2 != null && c1.SequenceEqual(c2)),
            c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
            c => new List<string>(c));

        modelBuilder.Entity<SiloRecord<DateTime>>(c =>
        {
            c.HasKey(p => new {p.ClusterId, p.Address, p.Port, p.Generation}).HasName("PK_Silo");
            c.Property(p => p.Address).HasMaxLength(45).IsRequired();
            c.Property(p => p.Port).IsRequired();
            c.Property(p => p.Generation).IsRequired();
            c.Property(p => p.Name).HasMaxLength(150).IsRequired();
            c.Property(p => p.HostName).HasMaxLength(150).IsRequired();
            c.Property(p => p.Status).IsRequired();
            c.Property(p => p.ProxyPort).IsRequired(false);
            c.Property(p => p.SuspectingTimes).IsRequired(false).HasConversion(listToStringConverter).Metadata.SetValueComparer(listComparer);
            c.Property(p => p.SuspectingSilos).IsRequired(false).HasConversion(listToStringConverter).Metadata.SetValueComparer(listComparer);
            c.Property(p => p.StartTime).IsRequired();
            c.Property(p => p.IAmAliveTime).IsRequired();
            c.Property(p => p.ETag).IsRowVersion().IsConcurrencyToken();

            c
                .HasOne(p => p.Cluster)
                .WithMany(p => p.Silos)
                .HasForeignKey(p => p.ClusterId);

            c.HasIndex(p => p.ClusterId).HasDatabaseName("IDX_Silo_ClusterId");
            c.HasIndex(p => new {p.ClusterId, p.Status}).HasDatabaseName("IDX_Silo_ClusterId_Status");
            c.HasIndex(p => new {p.ClusterId, p.Status, p.IAmAliveTime}).HasDatabaseName("IDX_Silo_ClusterId_Status_IAmAlive");
        });
    }

    // public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = new CancellationToken())
    // {
    //     foreach (var entity in ChangeTracker.Entries().Where(e => e.State is EntityState.Modified or EntityState.Added))
    //     {
    //         switch (entity.Entity)
    //         {
    //             case ClusterRecord<Guid> clusterRecord:
    //                 clusterRecord.ETag = Guid.NewGuid();
    //                 continue;
    //             case SiloRecord<Guid> siloRecord:
    //                 siloRecord.ETag = Guid.NewGuid();
    //                 continue;
    //         }
    //     }
    //
    //     return base.SaveChangesAsync(cancellationToken);
    // }
}