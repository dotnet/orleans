using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace Orleans.Clustering.EntityFrameworkCore.MySql.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialClusteringSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Clusters",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    ETag = table.Column<DateTime>(type: "datetime(6)", rowVersion: true, nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.ComputedColumn)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cluster", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Silos",
                columns: table => new
                {
                    ClusterId = table.Column<string>(type: "varchar(255)", nullable: false),
                    Address = table.Column<string>(type: "varchar(45)", maxLength: 45, nullable: false),
                    Port = table.Column<int>(type: "int", nullable: false),
                    Generation = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    HostName = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ProxyPort = table.Column<int>(type: "int", nullable: true),
                    SuspectingTimes = table.Column<string>(type: "longtext", nullable: true),
                    SuspectingSilos = table.Column<string>(type: "longtext", nullable: true),
                    StartTime = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                    IAmAliveTime = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                    ETag = table.Column<DateTime>(type: "datetime(6)", rowVersion: true, nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.ComputedColumn)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Silo", x => new { x.ClusterId, x.Address, x.Port, x.Generation });
                    table.ForeignKey(
                        name: "FK_Silos_Clusters_ClusterId",
                        column: x => x.ClusterId,
                        principalTable: "Clusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IDX_Silo_ClusterId",
                table: "Silos",
                column: "ClusterId");

            migrationBuilder.CreateIndex(
                name: "IDX_Silo_ClusterId_Status",
                table: "Silos",
                columns: new[] { "ClusterId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IDX_Silo_ClusterId_Status_IAmAlive",
                table: "Silos",
                columns: new[] { "ClusterId", "Status", "IAmAliveTime" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Silos");

            migrationBuilder.DropTable(
                name: "Clusters");
        }
    }
}
