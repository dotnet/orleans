﻿// <auto-generated />
using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Orleans.Clustering.EntityFrameworkCore.SqlServer.Data;

#nullable disable

namespace Orleans.Clustering.EntityFrameworkCore.SqlServer.Data.Migrations
{
    [DbContext(typeof(SqlServerClusterDbContext))]
    [Migration("20231005032242_InitialClusteringSchema")]
    partial class InitialClusteringSchema
    {
        /// <inheritdoc />
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "7.0.11")
                .HasAnnotation("Relational:MaxIdentifierLength", 128);

            SqlServerModelBuilderExtensions.UseIdentityColumns(modelBuilder);

            modelBuilder.Entity("Orleans.Clustering.EntityFrameworkCore.Data.ClusterRecord", b =>
                {
                    b.Property<string>("Id")
                        .HasColumnType("nvarchar(450)");

                    b.Property<byte[]>("ETag")
                        .IsConcurrencyToken()
                        .IsRequired()
                        .ValueGeneratedOnAddOrUpdate()
                        .HasColumnType("rowversion");

                    b.Property<DateTimeOffset>("Timestamp")
                        .HasColumnType("datetimeoffset");

                    b.Property<int>("Version")
                        .HasColumnType("int");

                    b.HasKey("Id")
                        .HasName("PK_Cluster");

                    SqlServerKeyBuilderExtensions.IsClustered(b.HasKey("Id"), false);

                    b.ToTable("Clusters");
                });

            modelBuilder.Entity("Orleans.Clustering.EntityFrameworkCore.Data.SiloRecord", b =>
                {
                    b.Property<string>("ClusterId")
                        .HasColumnType("nvarchar(450)");

                    b.Property<string>("Address")
                        .HasMaxLength(45)
                        .HasColumnType("nvarchar(45)");

                    b.Property<int>("Port")
                        .HasColumnType("int");

                    b.Property<int>("Generation")
                        .HasColumnType("int");

                    b.Property<byte[]>("ETag")
                        .IsConcurrencyToken()
                        .IsRequired()
                        .ValueGeneratedOnAddOrUpdate()
                        .HasColumnType("rowversion");

                    b.Property<string>("HostName")
                        .IsRequired()
                        .HasMaxLength(150)
                        .HasColumnType("nvarchar(150)");

                    b.Property<DateTimeOffset>("IAmAliveTime")
                        .HasColumnType("datetimeoffset");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasMaxLength(150)
                        .HasColumnType("nvarchar(150)");

                    b.Property<int?>("ProxyPort")
                        .HasColumnType("int");

                    b.Property<DateTimeOffset>("StartTime")
                        .HasColumnType("datetimeoffset");

                    b.Property<int>("Status")
                        .HasColumnType("int");

                    b.Property<string>("SuspectingSilos")
                        .HasColumnType("nvarchar(max)");

                    b.Property<string>("SuspectingTimes")
                        .HasColumnType("nvarchar(max)");

                    b.HasKey("ClusterId", "Address", "Port", "Generation")
                        .HasName("PK_Silo");

                    SqlServerKeyBuilderExtensions.IsClustered(b.HasKey("ClusterId", "Address", "Port", "Generation"), false);

                    b.HasIndex("ClusterId")
                        .HasDatabaseName("IDX_Silo_ClusterId");

                    SqlServerIndexBuilderExtensions.IsClustered(b.HasIndex("ClusterId"), false);

                    b.HasIndex("ClusterId", "Status")
                        .HasDatabaseName("IDX_Silo_ClusterId_Status");

                    SqlServerIndexBuilderExtensions.IsClustered(b.HasIndex("ClusterId", "Status"), false);

                    b.HasIndex("ClusterId", "Status", "IAmAliveTime")
                        .HasDatabaseName("IDX_Silo_ClusterId_Status_IAmAlive");

                    SqlServerIndexBuilderExtensions.IsClustered(b.HasIndex("ClusterId", "Status", "IAmAliveTime"), false);

                    b.ToTable("Silos");
                });

            modelBuilder.Entity("Orleans.Clustering.EntityFrameworkCore.Data.SiloRecord", b =>
                {
                    b.HasOne("Orleans.Clustering.EntityFrameworkCore.Data.ClusterRecord", "Cluster")
                        .WithMany("Silos")
                        .HasForeignKey("ClusterId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("Cluster");
                });

            modelBuilder.Entity("Orleans.Clustering.EntityFrameworkCore.Data.ClusterRecord", b =>
                {
                    b.Navigation("Silos");
                });
#pragma warning restore 612, 618
        }
    }
}
