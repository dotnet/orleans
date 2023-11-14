﻿// <auto-generated />
using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Orleans.Reminders.EntityFrameworkCore.SqlServer.Data;

#nullable disable

namespace Orleans.Reminders.EntityFrameworkCore.SqlServer.Data.Migrations
{
    [DbContext(typeof(SqlServerReminderDbContext))]
    [Migration("20231005034632_InitialRemindersSchema")]
    partial class InitialRemindersSchema
    {
        /// <inheritdoc />
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "7.0.11")
                .HasAnnotation("Relational:MaxIdentifierLength", 128);

            SqlServerModelBuilderExtensions.UseIdentityColumns(modelBuilder);

            modelBuilder.Entity("Orleans.Reminders.EntityFrameworkCore.Data.ReminderRecord", b =>
                {
                    b.Property<string>("ServiceId")
                        .HasColumnType("nvarchar(450)");

                    b.Property<string>("GrainId")
                        .HasColumnType("nvarchar(450)");

                    b.Property<string>("Name")
                        .HasColumnType("nvarchar(450)");

                    b.Property<byte[]>("ETag")
                        .IsConcurrencyToken()
                        .IsRequired()
                        .ValueGeneratedOnAddOrUpdate()
                        .HasColumnType("rowversion");

                    b.Property<long>("GrainHash")
                        .HasColumnType("bigint");

                    b.Property<TimeSpan>("Period")
                        .HasColumnType("time");

                    b.Property<DateTimeOffset>("StartAt")
                        .HasColumnType("datetimeoffset");

                    b.HasKey("ServiceId", "GrainId", "Name")
                        .HasName("PK_Reminders");

                    b.HasIndex("ServiceId", "GrainHash")
                        .HasDatabaseName("IDX_Reminders_ServiceId_GrainHash");

                    SqlServerIndexBuilderExtensions.IsClustered(b.HasIndex("ServiceId", "GrainHash"), false);

                    b.HasIndex("ServiceId", "GrainId")
                        .HasDatabaseName("IDX_Reminders_ServiceId_GrainId");

                    SqlServerIndexBuilderExtensions.IsClustered(b.HasIndex("ServiceId", "GrainId"), false);

                    b.ToTable("Reminders");
                });
#pragma warning restore 612, 618
        }
    }
}
