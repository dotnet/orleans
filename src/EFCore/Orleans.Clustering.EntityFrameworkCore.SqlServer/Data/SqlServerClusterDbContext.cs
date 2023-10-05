using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Orleans.Clustering.EntityFrameworkCore.Data;

namespace Orleans.Clustering.EntityFrameworkCore.SqlServer.Data;

public sealed class SqlServerClusterDbContext : ClusterDbContext<SqlServerClusterDbContext>
{
    public SqlServerClusterDbContext(DbContextOptions<SqlServerClusterDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ClusterRecord>(c =>
        {
            c.HasKey(p => p.Id).IsClustered(false).HasName("PK_Cluster");
            c.Property(p => p.Timestamp).IsRequired();
            c.Property(p => p.Version).IsRequired();
            c.Property(p => p.ETag).IsRequired().IsRowVersion();

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

        modelBuilder.Entity<SiloRecord>(c =>
        {
            c.HasKey(p => new {p.ClusterId, p.Address, p.Port, p.Generation}).IsClustered(false).HasName("PK_Silo");
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
            c.Property(p => p.ETag).IsRequired().IsRowVersion();

            c
                .HasOne(p => p.Cluster)
                .WithMany(p => p.Silos)
                .HasForeignKey(p => p.ClusterId);

            c.HasIndex(p => p.ClusterId).IsClustered(false).HasDatabaseName("IDX_Silo_ClusterId");
            c.HasIndex(p => new {p.ClusterId, p.Status}).IsClustered(false).HasDatabaseName("IDX_Silo_ClusterId_Status");
            c.HasIndex(p => new {p.ClusterId, p.Status, p.IAmAliveTime}).IsClustered(false).HasDatabaseName("IDX_Silo_ClusterId_Status_IAmAlive");
        });
    }
}