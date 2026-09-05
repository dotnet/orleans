using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orleans.Reminders.EntityFrameworkCore.PostgreSQL.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialRemindersSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Reminders",
                columns: table => new
                {
                    ServiceId = table.Column<string>(type: "text", nullable: false),
                    GrainId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    StartAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Period = table.Column<long>(type: "bigint", nullable: false),
                    GrainHash = table.Column<long>(type: "bigint", nullable: false),
                    ETag = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reminders", x => new { x.ServiceId, x.GrainId, x.Name });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Reminders_ServiceId_GrainHash",
                table: "Reminders",
                columns: new[] { "ServiceId", "GrainHash" });

            migrationBuilder.CreateIndex(
                name: "IX_Reminders_ServiceId_GrainId",
                table: "Reminders",
                columns: new[] { "ServiceId", "GrainId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Reminders");
        }
    }
}
