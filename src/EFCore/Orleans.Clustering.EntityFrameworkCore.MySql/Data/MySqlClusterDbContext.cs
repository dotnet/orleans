using Microsoft.EntityFrameworkCore;
using Orleans.Clustering.EntityFrameworkCore.Data;

namespace Orleans.Clustering.EntityFrameworkCore.MySql.Data;

public class MySqlClusterDbContext : GuidClusterDbContext<MySqlClusterDbContext>
{
    private const string IdentifierCollation = "utf8mb4_bin";

    public MySqlClusterDbContext(DbContextOptions<MySqlClusterDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ClusterRecord<Guid>>()
            .Property(record => record.Id)
            .UseCollation(IdentifierCollation);
        modelBuilder.Entity<SiloRecord<Guid>>(entity =>
        {
            entity.Property(record => record.ClusterId).UseCollation(IdentifierCollation);
            entity.Property(record => record.Address).UseCollation(IdentifierCollation);
        });
    }
}