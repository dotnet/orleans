using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orleans.Reminders.EntityFrameworkCore.SqlServer.Data.Migrations
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
                    ServiceIdHash = table.Column<byte[]>(type: "binary(32)", maxLength: 32, nullable: false),
                    GrainIdHash = table.Column<byte[]>(type: "binary(32)", maxLength: 32, nullable: false),
                    ReminderNameHash = table.Column<byte[]>(type: "binary(32)", maxLength: 32, nullable: false),
                    ServiceId = table.Column<string>(type: "nvarchar(max)", nullable: false, collation: "Latin1_General_100_BIN2"),
                    GrainId = table.Column<string>(type: "nvarchar(max)", nullable: false, collation: "Latin1_General_100_BIN2"),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false, collation: "Latin1_General_100_BIN2"),
                    StartAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Period = table.Column<long>(type: "bigint", nullable: false),
                    GrainHash = table.Column<long>(type: "bigint", nullable: false),
                    ETag = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reminders", x => new { x.ServiceIdHash, x.GrainIdHash, x.ReminderNameHash })
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateIndex(
                name: "IDX_Reminders_ServiceIdHash_GrainHash",
                table: "Reminders",
                columns: new[] { "ServiceIdHash", "GrainHash" })
                .Annotation("SqlServer:Clustered", false);

            migrationBuilder.CreateIndex(
                name: "IDX_Reminders_ServiceIdHash_GrainIdHash",
                table: "Reminders",
                columns: new[] { "ServiceIdHash", "GrainIdHash" })
                .Annotation("SqlServer:Clustered", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Reminders");
        }
    }
}
