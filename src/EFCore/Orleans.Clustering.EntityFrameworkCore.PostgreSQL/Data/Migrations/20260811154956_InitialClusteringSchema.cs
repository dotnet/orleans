using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orleans.Clustering.EntityFrameworkCore.PostgreSQL.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialClusteringSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Clusters",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    ETag = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Clusters", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Silos",
                columns: table => new
                {
                    ClusterId = table.Column<string>(type: "text", nullable: false),
                    Address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    Generation = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    HostName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ProxyPort = table.Column<int>(type: "integer", nullable: true),
                    SuspectingTimes = table.Column<string>(type: "text", nullable: true),
                    SuspectingSilos = table.Column<string>(type: "text", nullable: true),
                    StartTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IAmAliveTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ETag = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Silos", x => new { x.ClusterId, x.Address, x.Port, x.Generation });
                    table.ForeignKey(
                        name: "FK_Silos_Clusters_ClusterId",
                        column: x => x.ClusterId,
                        principalTable: "Clusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Silos_ClusterId",
                table: "Silos",
                column: "ClusterId");

            migrationBuilder.CreateIndex(
                name: "IX_Silos_ClusterId_Status",
                table: "Silos",
                columns: new[] { "ClusterId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Silos_ClusterId_Status_IAmAliveTime",
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
