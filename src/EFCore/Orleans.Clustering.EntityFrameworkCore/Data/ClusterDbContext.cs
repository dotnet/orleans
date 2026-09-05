using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Orleans.Clustering.EntityFrameworkCore.Data;

public class ClusterDbContext<TDbContext, TETag> : DbContext where TDbContext : DbContext
{
    public DbSet<ClusterRecord<TETag>> Clusters { get; set; } = default!;
    public DbSet<SiloRecord<TETag>> Silos { get; set; } = default!;

    public ClusterDbContext(DbContextOptions<TDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var listConverter = new ValueConverter<List<string>, string?>(
            value => value == null ? null : JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
            value => string.IsNullOrEmpty(value)
                ? new List<string>()
                : JsonSerializer.Deserialize<List<string>>(value, (JsonSerializerOptions?)null) ?? new List<string>());
        var listComparer = new ValueComparer<List<string>>(
            (left, right) => ReferenceEquals(left, right) || left != null && right != null && left.SequenceEqual(right),
            value => value == null ? 0 : value.Aggregate(0, (hash, item) => HashCode.Combine(hash, item)),
            value => value == null ? new List<string>() : value.ToList());

        modelBuilder.Entity<ClusterRecord<TETag>>(c =>
        {
            c.HasKey(p => p.Id);
            c.Property(p => p.Timestamp).IsRequired();
            c.Property(p => p.Version).IsRequired();
            c.Property(p => p.ETag).IsRequired().IsConcurrencyToken();

            c
                .HasMany(p => p.Silos)
                .WithOne(r => r.Cluster)
                .HasForeignKey(r => r.ClusterId);
        });

        modelBuilder.Entity<SiloRecord<TETag>>(c =>
        {
            c.HasKey(p => new { p.ClusterId, p.Address, p.Port, p.Generation });
            c.Property(p => p.Address).HasMaxLength(45).IsRequired();
            c.Property(p => p.Port).IsRequired();
            c.Property(p => p.Generation).IsRequired();
            c.Property(p => p.Name).HasMaxLength(150).IsRequired();
            c.Property(p => p.HostName).HasMaxLength(150).IsRequired();
            c.Property(p => p.Status).IsRequired();
            c.Property(p => p.ProxyPort).IsRequired(false);
            c.Property(p => p.SuspectingTimes)
                .IsRequired(false)
                .HasConversion(listConverter)
                .Metadata.SetValueComparer(listComparer);
            c.Property(p => p.SuspectingSilos)
                .IsRequired(false)
                .HasConversion(listConverter)
                .Metadata.SetValueComparer(listComparer);
            c.Property(p => p.StartTime).IsRequired();
            c.Property(p => p.IAmAliveTime).IsRequired();
            c.Property(p => p.ETag).IsRequired().IsConcurrencyToken();

            c
                .HasOne(p => p.Cluster)
                .WithMany(p => p.Silos)
                .HasForeignKey(p => p.ClusterId);
        });
    }
}
