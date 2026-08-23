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
                    ServiceIdHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    GrainIdHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    ReminderNameHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
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
                    table.PrimaryKey("PK_Reminders", x => new { x.ServiceIdHash, x.GrainIdHash, x.ReminderNameHash });
                    table.CheckConstraint("CK_Reminders_GrainIdHash_Length", "octet_length(\"GrainIdHash\") = 32");
                    table.CheckConstraint("CK_Reminders_ReminderNameHash_Length", "octet_length(\"ReminderNameHash\") = 32");
                    table.CheckConstraint("CK_Reminders_ServiceIdHash_Length", "octet_length(\"ServiceIdHash\") = 32");
                });

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
