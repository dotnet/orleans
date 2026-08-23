using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orleans.Reminders.EntityFrameworkCore.MySql.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialRemindersSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Reminders",
                columns: table => new
                {
                    ServiceIdHash = table.Column<byte[]>(type: "binary(32)", maxLength: 32, nullable: false),
                    GrainIdHash = table.Column<byte[]>(type: "binary(32)", maxLength: 32, nullable: false),
                    ReminderNameHash = table.Column<byte[]>(type: "binary(32)", maxLength: 32, nullable: false),
                    ServiceId = table.Column<string>(type: "longtext", nullable: false, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    GrainId = table.Column<string>(type: "longtext", nullable: false, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Name = table.Column<string>(type: "longtext", nullable: false, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    StartAt = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                    Period = table.Column<long>(type: "bigint", nullable: false),
                    GrainHash = table.Column<uint>(type: "int unsigned", nullable: false),
                    ETag = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reminders", x => new { x.ServiceIdHash, x.GrainIdHash, x.ReminderNameHash });
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Reminders_ServiceIdHash_GrainHash",
                table: "Reminders",
                columns: new[] { "ServiceIdHash", "GrainHash" });

            migrationBuilder.CreateIndex(
                name: "IX_Reminders_ServiceIdHash_GrainIdHash",
                table: "Reminders",
                columns: new[] { "ServiceIdHash", "GrainIdHash" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Reminders");
        }
    }
}
